using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarketplaceSync.Services.Ai;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Orders;
using MarketplaceSync.Web.Models.PostSale;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.PostSale;

public sealed record ConversationMessage(string Id, bool FromSeller, string Text, DateTime SentAt, string? Status);

public sealed record ClaimMessage(string SenderRole, string Text, DateTime SentAt);

public sealed record PostSaleSyncResult(int Messages, int Claims, IReadOnlyList<string> Errors);

/// <summary>Conversación sin responder: el último mensaje del paquete es del comprador.</summary>
public sealed record PendingConversation(int? SalesOrderId, string PackId, string? OrderId, string? Buyer, string? ItemTitle, string LastText, DateTime LastAt);

/// <summary>
/// Postventa de Mercado Libre: mensajes con el comprador (/messages/packs/...) y reclamos (/post-purchase/v1/claims).
/// Guarda lo que llega para avisar (Hoy, Telegram) y deja contestar desde la app.
/// </summary>
public sealed class PostSaleService
{
    /// <summary>Mercado Libre corta los mensajes de postventa a 350 caracteres.</summary>
    public const int MaxMessageLength = 350;

    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    private readonly AppDbContext _context;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IMercadoLibreQuestionService _questions;
    private readonly ClaudeClient _claude;
    private readonly ILogger<PostSaleService> _logger;

    public PostSaleService(
        AppDbContext context,
        IMercadoLibreAuthService auth,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IMercadoLibreQuestionService questions,
        ClaudeClient claude,
        ILogger<PostSaleService> logger)
    {
        _context = context;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _questions = questions;
        _claude = claude;
        _logger = logger;
    }

    private string ApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } v ? v.TrimEnd('/') : "https://api.mercadolibre.com";

    public static string PackOf(SalesOrder order) => string.IsNullOrWhiteSpace(order.ExternalPackId) ? order.ExternalOrderId : order.ExternalPackId!;

    public async Task<IReadOnlyList<Guid>> GetOrganizationsAsync(CancellationToken cancellationToken = default) =>
        await _context.MarketplaceAccounts.AsNoTracking()
            .Where(a => a.Marketplace == "MercadoLibre" && a.Status == "Connected")
            .Select(a => a.OrganizationId).Distinct().ToListAsync(cancellationToken);

    // ─────────────────────────────────────────────────────────────
    // Sincronizar (cada 10 min y al llegar una notificación)
    // ─────────────────────────────────────────────────────────────

    public async Task<PostSaleSyncResult> SyncAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var accounts = await _context.MarketplaceAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && a.Marketplace == "MercadoLibre" && a.Status == "Connected")
            .Select(a => new { a.Id, a.ExternalAccountId, a.DisplayName })
            .ToListAsync(cancellationToken);
        var errors = new List<string>();
        int messages = 0, claims = 0;

        foreach (var account in accounts)
        {
            string token;
            try
            {
                token = await _auth.GetValidAccessTokenAsync(organizationId, account.Id);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "No token for post-sale sync {Account}.", account.Id);
                errors.Add($"{account.DisplayName}: vuelve a conectar la cuenta.");
                continue;
            }

            // Paquetes con mensajes sin leer + los que siguen sin respuesta en la app.
            try
            {
                var packs = new HashSet<string>();
                using (var unread = await GetJsonAsync(token, "/messages/unread?role=seller&tag=post_sale", cancellationToken))
                {
                    if (unread?.RootElement.TryGetProperty("results", out var results) == true && results.ValueKind == JsonValueKind.Array)
                        foreach (var r in results.EnumerateArray())
                            if (Str(r, "resource") is { } resource && resource.Split('/', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 2 } parts && parts[0] == "packs")
                                packs.Add(parts[1]);
                }
                foreach (var pending in (await GetPendingAsync(organizationId, 30, cancellationToken)).Where(p => p.SalesOrderId.HasValue))
                    packs.Add(pending.PackId);

                foreach (var pack in packs.Take(40))
                    messages += await RefreshPackAsync(organizationId, account.Id, account.ExternalAccountId, token, pack, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "Messages sync failed for {Account}.", account.Id);
                errors.Add($"{account.DisplayName}: Mercado Libre no devolvió los mensajes.");
            }

            try
            {
                claims += await RefreshClaimsAsync(organizationId, account.Id, token, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "Claims sync failed for {Account}.", account.Id);
                errors.Add($"{account.DisplayName}: Mercado Libre no devolvió los reclamos.");
            }
        }

        try
        {
            await SendThankYousAsync(organizationId, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Thank-you messages failed for {Org}.", organizationId);
        }
        return new PostSaleSyncResult(messages, claims, errors);
    }

    /// <summary>
    /// Mensaje de gracias a cada venta pagada nueva (después de prenderlo, de las últimas 48 h, uno por carrito).
    /// Primero como mensaje normal; si Mercado Libre no deja iniciar la conversación, con "mensajes con opciones"
    /// (<c>/messages/action_guide/packs/{pack}/option</c>, opción OTHER). Devuelve cuántos salieron.
    /// </summary>
    public async Task<int> SendThankYousAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var settings = await _context.AutomationSettings.AsNoTracking().FirstOrDefaultAsync(a => a.OrganizationId == org, cancellationToken);
        if (settings is not { ThankYouEnabled: true } || string.IsNullOrWhiteSpace(settings.ThankYouMessage))
            return 0;
        var since = new[] { settings.ThankYouSince ?? DateTime.UtcNow, DateTime.UtcNow.AddHours(-48) }.Max();
        var orders = await _context.SalesOrders.Include(o => o.MarketplaceAccount)
            .Where(o => o.OrganizationId == org && o.Status == "paid" && o.ThankYouSentAt == null && o.DateCreated >= since && o.BuyerId != null)
            .OrderBy(o => o.DateCreated).Take(20)
            .ToListAsync(cancellationToken);
        var (text, _) = CleanContact(settings.ThankYouMessage);
        text = text.Trim();
        if (text.Length == 0 || text.Length > MaxMessageLength)
            return 0;

        var done = new HashSet<string>();
        var sent = 0;
        foreach (var order in orders)
        {
            var pack = PackOf(order);
            if (!done.Add(order.MarketplaceAccountId + ":" + pack))
            {
                order.ThankYouSentAt = DateTime.UtcNow;
                order.ThankYouStatus = "same_pack";
                continue;
            }
            // Si ya hay mensajes en el paquete (el comprador escribió o ya se le escribió), no se manda.
            if (await _context.OrderMessages.AnyAsync(m => m.OrganizationId == org && m.PackId == pack, cancellationToken))
            {
                order.ThankYouSentAt = DateTime.UtcNow;
                order.ThankYouStatus = "skipped";
                continue;
            }
            var ok = false;
            try
            {
                var token = await _auth.GetValidAccessTokenAsync(org, order.MarketplaceAccountId);
                var seller = order.MarketplaceAccount.ExternalAccountId;
                var (normal, content) = await SendJsonAsync(token, HttpMethod.Post,
                    $"/messages/packs/{Uri.EscapeDataString(pack)}/sellers/{Uri.EscapeDataString(seller)}?tag=post_sale",
                    new { from = new { user_id = seller }, to = new { user_id = order.BuyerId }, text }, cancellationToken);
                ok = normal;
                if (!ok)
                    (ok, _) = await SendJsonAsync(token, HttpMethod.Post,
                        $"/messages/action_guide/packs/{Uri.EscapeDataString(pack)}/option",
                        new { option_id = "OTHER", text }, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "Thank-you failed for order {Order}.", order.Id);
            }
            order.ThankYouSentAt = DateTime.UtcNow;
            order.ThankYouStatus = ok ? "sent" : "failed";
            if (ok)
            {
                sent++;
                _context.OrderMessages.Add(new OrderMessage
                {
                    OrganizationId = org, MarketplaceAccountId = order.MarketplaceAccountId, PackId = pack, SalesOrderId = order.Id,
                    ExternalId = "thanks-" + Guid.NewGuid().ToString("N"), FromSeller = true, Text = text, SentAt = DateTime.UtcNow, Status = "available"
                });
            }
        }
        await _context.SaveChangesAsync(cancellationToken);
        return sent;
    }

    /// <summary>Trae los mensajes de un paquete y guarda los nuevos. Devuelve cuántos eran nuevos.</summary>
    private async Task<int> RefreshPackAsync(Guid org, int accountId, string sellerId, string token, string pack, CancellationToken cancellationToken)
    {
        using var doc = await GetJsonAsync(token, $"/messages/packs/{Uri.EscapeDataString(pack)}/sellers/{Uri.EscapeDataString(sellerId)}?tag=post_sale&mark_as_read=false&limit=50", cancellationToken);
        if (doc is null || !doc.RootElement.TryGetProperty("messages", out var list) || list.ValueKind != JsonValueKind.Array)
            return 0;

        var orderId = await _context.SalesOrders.AsNoTracking()
            .Where(o => o.OrganizationId == org && (o.ExternalPackId == pack || o.ExternalOrderId == pack))
            .Select(o => (int?)o.Id).FirstOrDefaultAsync(cancellationToken);
        var existing = (await _context.OrderMessages.AsNoTracking()
            .Where(m => m.OrganizationId == org && m.PackId == pack).Select(m => m.ExternalId).ToListAsync(cancellationToken)).ToHashSet();

        var added = 0;
        foreach (var m in list.EnumerateArray())
        {
            var id = Str(m, "id");
            if (id is null || existing.Contains(id)) continue;
            var from = m.TryGetProperty("from", out var f) ? (f.TryGetProperty("user_id", out var u) ? u.ToString() : null) : null;
            var created = m.TryGetProperty("message_date", out var md) ? Date(md, "created") ?? Date(md, "received") : null;
            var text = Str(m, "text") ?? string.Empty;
            _context.OrderMessages.Add(new OrderMessage
            {
                OrganizationId = org,
                MarketplaceAccountId = accountId,
                PackId = pack,
                SalesOrderId = orderId,
                ExternalId = id,
                FromSeller = from == sellerId,
                Text = text.Length > 4000 ? text[..4000] : text,
                SentAt = created ?? DateTime.UtcNow,
                Status = m.TryGetProperty("message_moderation", out var mod) ? Str(mod, "status") : Str(m, "status")
            });
            existing.Add(id);
            added++;
        }
        if (added > 0)
            await _context.SaveChangesAsync(cancellationToken);
        return added;
    }

    private async Task<int> RefreshClaimsAsync(Guid org, int accountId, string token, CancellationToken cancellationToken)
    {
        var seen = new List<JsonElement>();
        using var open = await GetJsonAsync(token, "/post-purchase/v1/claims/search?status=opened&limit=50&sort=last_updated:desc", cancellationToken);
        if (open?.RootElement.TryGetProperty("data", out var data) == true && data.ValueKind == JsonValueKind.Array)
            seen.AddRange(data.EnumerateArray().Select(x => x.Clone()));

        // Los que teníamos abiertos y ya no aparecen: se consultan uno por uno para saber cómo cerraron.
        var openIds = seen.Select(c => c.TryGetProperty("id", out var i) ? i.ToString() : null).Where(x => x is not null).ToHashSet();
        var stale = await _context.OrderClaims.AsNoTracking()
            .Where(c => c.OrganizationId == org && c.MarketplaceAccountId == accountId && c.Status == "opened" && !openIds.Contains(c.ExternalId))
            .Select(c => c.ExternalId).Take(20).ToListAsync(cancellationToken);
        foreach (var id in stale)
        {
            using var one = await GetJsonAsync(token, $"/post-purchase/v1/claims/{Uri.EscapeDataString(id)}", cancellationToken);
            if (one is not null) seen.Add(one.RootElement.Clone());
        }

        var changed = 0;
        foreach (var c in seen)
        {
            var id = c.TryGetProperty("id", out var idEl) ? idEl.ToString() : null;
            if (id is null) continue;
            var claim = await _context.OrderClaims.FirstOrDefaultAsync(x => x.OrganizationId == org && x.ExternalId == id, cancellationToken);
            if (claim is null)
            {
                claim = new OrderClaim { OrganizationId = org, MarketplaceAccountId = accountId, ExternalId = id };
                _context.OrderClaims.Add(claim);
            }
            MapClaim(claim, c);
            if (claim.ResourceId is not null && claim.SalesOrderId is null)
                claim.SalesOrderId = await _context.SalesOrders.AsNoTracking()
                    .Where(o => o.OrganizationId == org && o.ExternalOrderId == claim.ResourceId).Select(o => (int?)o.Id).FirstOrDefaultAsync(cancellationToken);
            claim.SyncedAt = DateTime.UtcNow;
            changed++;
        }
        if (changed > 0)
            await _context.SaveChangesAsync(cancellationToken);
        return changed;
    }

    private static void MapClaim(OrderClaim claim, JsonElement c)
    {
        claim.ResourceId = c.TryGetProperty("resource_id", out var r) ? r.ToString() : claim.ResourceId;
        claim.Type = Str(c, "type") ?? claim.Type;
        claim.Stage = Str(c, "stage") ?? claim.Stage;
        claim.Status = Str(c, "status") ?? claim.Status;
        claim.ReasonId = Str(c, "reason_id") ?? claim.ReasonId;
        claim.DateCreated = Date(c, "date_created") ?? (claim.DateCreated == default ? DateTime.UtcNow : claim.DateCreated);
        claim.LastUpdated = Date(c, "last_updated") ?? DateTime.UtcNow;
        if (c.TryGetProperty("related_entities", out var rel) && rel.ValueKind == JsonValueKind.Array &&
            rel.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "return"))
            claim.HasReturn = true;
        if (c.TryGetProperty("resolution", out var res) && res.ValueKind == JsonValueKind.Object)
            claim.Resolution = Str(res, "reason") ?? Str(res, "benefited");

        claim.DueDate = null;
        claim.PendingActions = null;
        if (c.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Array)
        {
            var me = players.EnumerateArray().FirstOrDefault(p => Str(p, "role") == "respondent" || Str(p, "type") == "seller");
            if (me.ValueKind == JsonValueKind.Object && me.TryGetProperty("available_actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
            {
                var list = actions.EnumerateArray().ToList();
                claim.PendingActions = string.Join(",", list.Select(a => Str(a, "action")).Where(x => x is not null)).Truncate(200);
                claim.DueDate = list.Where(a => a.TryGetProperty("mandatory", out var m) && m.ValueKind == JsonValueKind.True)
                    .Select(a => Date(a, "due_date")).Where(d => d.HasValue).Min()
                    ?? list.Select(a => Date(a, "due_date")).Where(d => d.HasValue).Min();
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Pantallas
    // ─────────────────────────────────────────────────────────────

    /// <summary>Paquetes cuyo último mensaje es del comprador (hay que contestar).</summary>
    public async Task<IReadOnlyList<PendingConversation>> GetPendingAsync(Guid org, int take = 50, CancellationToken cancellationToken = default)
    {
        var since = DateTime.UtcNow.AddDays(-45);
        var recent = await _context.OrderMessages.AsNoTracking()
            .Where(m => m.OrganizationId == org && m.SentAt >= since)
            .Select(m => new { m.PackId, m.SalesOrderId, m.FromSeller, m.Text, m.SentAt })
            .ToListAsync(cancellationToken);
        var last = recent.GroupBy(m => m.PackId)
            .Select(g => g.OrderByDescending(m => m.SentAt).First())
            .Where(m => !m.FromSeller)
            .OrderByDescending(m => m.SentAt)
            .Take(take)
            .ToList();
        var orderIds = last.Where(m => m.SalesOrderId.HasValue).Select(m => m.SalesOrderId!.Value).Distinct().ToList();
        var orders = await _context.SalesOrders.AsNoTracking().Include(o => o.Items)
            .Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, cancellationToken);
        return last.Select(m =>
        {
            orders.TryGetValue(m.SalesOrderId ?? 0, out var o);
            return new PendingConversation(m.SalesOrderId, m.PackId, o?.ExternalOrderId, o?.BuyerNickname, o?.Items.FirstOrDefault()?.Title, m.Text, m.SentAt);
        }).ToList();
    }

    /// <summary>Conversación en vivo de un pedido (y se guarda lo nuevo).</summary>
    public async Task<(IReadOnlyList<ConversationMessage> Messages, string? Error)> GetConversationAsync(Guid org, int orderId, CancellationToken cancellationToken = default)
    {
        var order = await _context.SalesOrders.AsNoTracking().Include(o => o.MarketplaceAccount)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == org, cancellationToken)
            ?? throw new KeyNotFoundException();
        var pack = PackOf(order);
        string? error = null;
        try
        {
            var token = await _auth.GetValidAccessTokenAsync(org, order.MarketplaceAccountId);
            await RefreshPackAsync(org, order.MarketplaceAccountId, order.MarketplaceAccount.ExternalAccountId, token, pack, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Live conversation failed for order {Order}.", orderId);
            error = "No pude leer los mensajes nuevos de Mercado Libre; te muestro los guardados.";
        }

        var messages = await _context.OrderMessages.AsNoTracking()
            .Where(m => m.OrganizationId == org && m.PackId == pack)
            .OrderBy(m => m.SentAt)
            .Select(m => new ConversationMessage(m.ExternalId, m.FromSeller, m.Text, m.SentAt, m.Status))
            .ToListAsync(cancellationToken);
        return (messages, error);
    }

    /// <summary>Envía un mensaje al comprador. Devuelve null si salió bien o el motivo en palabras simples.</summary>
    public async Task<string?> SendMessageAsync(Guid org, int orderId, string text, CancellationToken cancellationToken = default)
    {
        var order = await _context.SalesOrders.AsNoTracking().Include(o => o.MarketplaceAccount)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == org, cancellationToken)
            ?? throw new KeyNotFoundException();
        var (clean, removed) = CleanContact(text ?? string.Empty);
        clean = clean.Trim();
        if (clean.Length == 0)
            return removed ? "El mensaje solo tenía datos de contacto; Mercado Libre no los permite." : "Escribe el mensaje.";
        if (clean.Length > MaxMessageLength)
            return $"El mensaje tiene {clean.Length} caracteres; Mercado Libre acepta hasta {MaxMessageLength}.";
        if (string.IsNullOrWhiteSpace(order.BuyerId))
            return "Este pedido no tiene comprador; sincroniza los pedidos e intenta de nuevo.";

        var sellerId = order.MarketplaceAccount.ExternalAccountId;
        var pack = PackOf(order);
        var token = await _auth.GetValidAccessTokenAsync(org, order.MarketplaceAccountId);
        var body = new { from = new { user_id = sellerId }, to = new { user_id = order.BuyerId }, text = clean };
        var (ok, content) = await SendJsonAsync(token, HttpMethod.Post,
            $"/messages/packs/{Uri.EscapeDataString(pack)}/sellers/{Uri.EscapeDataString(sellerId)}?tag=post_sale", body, cancellationToken);
        if (!ok)
            return "Mercado Libre no envió el mensaje: " + MercadoLibreListingClient.DescribeError(content);

        string id = Guid.NewGuid().ToString("N");
        try
        {
            using var doc = JsonDocument.Parse(content);
            id = Str(doc.RootElement, "id") ?? id;
        }
        catch (JsonException) { }

        _context.OrderMessages.Add(new OrderMessage
        {
            OrganizationId = org,
            MarketplaceAccountId = order.MarketplaceAccountId,
            PackId = pack,
            SalesOrderId = order.Id,
            ExternalId = id,
            FromSeller = true,
            Text = clean,
            SentAt = DateTime.UtcNow,
            Status = "available"
        });
        await _context.SaveChangesAsync(cancellationToken);
        return null;
    }

    /// <summary>Respuesta sugerida por la IA para el último mensaje del comprador.</summary>
    public async Task<string?> SuggestReplyAsync(Guid org, int orderId, CancellationToken cancellationToken = default)
    {
        if (!_claude.IsConfigured)
            return null;
        var order = await _context.SalesOrders.AsNoTracking().Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == org, cancellationToken);
        if (order is null)
            return null;
        var pack = PackOf(order);
        var messages = await _context.OrderMessages.AsNoTracking()
            .Where(m => m.OrganizationId == org && m.PackId == pack)
            .OrderByDescending(m => m.SentAt).Take(8).ToListAsync(cancellationToken);
        if (messages.Count == 0)
            return null;

        var prompt = new StringBuilder();
        prompt.AppendLine("Pedido: " + string.Join("; ", order.Items.Select(i => $"{i.Quantity}× {i.Title}")));
        prompt.AppendLine($"Estado del envío: {order.ShippingStatus ?? "sin dato"} {order.ShippingSubstatus}");
        prompt.AppendLine("Conversación (más antigua primero):");
        foreach (var m in messages.AsEnumerable().Reverse())
            prompt.AppendLine((m.FromSeller ? "Vendedor: " : "Comprador: ") + m.Text);
        var templates = await _context.MessageTemplates.AsNoTracking().Where(t => t.OrganizationId == org)
            .OrderBy(t => t.SortOrder).Take(20).ToListAsync(cancellationToken);
        if (templates.Count > 0)
        {
            prompt.AppendLine("Respuestas que el vendedor tiene guardadas (si una aplica, úsala adaptada; respeta su tono):");
            foreach (var t in templates)
                prompt.AppendLine("- " + MessageTemplateService.Render(t.Text, order));
        }
        var reply = await _claude.CompleteAsync(
            "Eres el vendedor de una tienda de refacciones en Mercado Libre México. Escribe la respuesta al último mensaje del comprador: " +
            "amable, clara, en español de México, máximo 300 caracteres, sin teléfonos, correos, links ni datos de contacto, " +
            "sin prometer fechas que no sabes. Si piden algo que no puedes saber, pregunta lo necesario. Devuelve solo el texto.",
            prompt.ToString(), 300, 0.3, cancellationToken);
        if (string.IsNullOrWhiteSpace(reply))
            return null;
        var (clean, _) = CleanContact(reply.Replace("**", "").Replace("__", "").Trim().Trim('"'));
        return clean.Length > MaxMessageLength ? clean[..MaxMessageLength] : clean;
    }

    public async Task<(OrderClaim? Claim, IReadOnlyList<ClaimMessage> Messages, string? Error)> GetClaimAsync(Guid org, long claimId, CancellationToken cancellationToken = default)
    {
        var claim = await _context.OrderClaims.FirstOrDefaultAsync(c => c.Id == claimId && c.OrganizationId == org, cancellationToken);
        if (claim is null)
            return (null, Array.Empty<ClaimMessage>(), null);
        var messages = new List<ClaimMessage>();
        string? error = null;
        try
        {
            var token = await _auth.GetValidAccessTokenAsync(org, claim.MarketplaceAccountId);
            using (var live = await GetJsonAsync(token, $"/post-purchase/v1/claims/{Uri.EscapeDataString(claim.ExternalId)}", cancellationToken))
            {
                if (live is not null)
                {
                    MapClaim(claim, live.RootElement);
                    claim.SyncedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync(cancellationToken);
                }
            }
            using var doc = await GetJsonAsync(token, $"/post-purchase/v1/claims/{Uri.EscapeDataString(claim.ExternalId)}/messages", cancellationToken);
            var list = doc?.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc?.RootElement.TryGetProperty("data", out var d) == true ? d : default;
            if (list.ValueKind == JsonValueKind.Array)
                messages.AddRange(list.EnumerateArray()
                    .Select(m => new ClaimMessage(Str(m, "sender_role") ?? "", Str(m, "message") ?? "", Date(m, "date_created") ?? DateTime.UtcNow))
                    .OrderBy(m => m.SentAt));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Claim detail failed {Claim}.", claimId);
            error = "No pude leer el reclamo en Mercado Libre ahora; te muestro lo guardado.";
        }
        return (claim, messages, error);
    }

    /// <summary>Mensaje al comprador dentro del reclamo (o al mediador si ya es disputa).</summary>
    public async Task<string?> SendClaimMessageAsync(Guid org, long claimId, string text, CancellationToken cancellationToken = default)
    {
        var claim = await _context.OrderClaims.AsNoTracking().FirstOrDefaultAsync(c => c.Id == claimId && c.OrganizationId == org, cancellationToken)
                    ?? throw new KeyNotFoundException();
        var (clean, removed) = CleanContact(text ?? string.Empty);
        clean = clean.Trim();
        if (clean.Length == 0)
            return removed ? "El mensaje solo tenía datos de contacto; Mercado Libre no los permite." : "Escribe el mensaje.";
        if (clean.Length > 2000)
            return "El mensaje es muy largo (máximo 2,000 caracteres).";

        var token = await _auth.GetValidAccessTokenAsync(org, claim.MarketplaceAccountId);
        var receiver = claim.Stage == "dispute" ? "mediator" : "complainant";
        var (ok, content) = await SendJsonAsync(token, HttpMethod.Post,
            $"/post-purchase/v1/claims/{Uri.EscapeDataString(claim.ExternalId)}/actions/send-message",
            new { receiver_role = receiver, message = clean }, cancellationToken);
        return ok ? null : "Mercado Libre no envió el mensaje: " + MercadoLibreListingClient.DescribeError(content);
    }

    /// <summary>
    /// Quita la frase completa que trae teléfono, correo o link (Mercado Libre bloquea el mensaje entero),
    /// para que no queden pedazos como "Mi cel es".
    /// </summary>
    /// <summary>El texto trae teléfono, correo o link (Mercado Libre lo bloquearía).</summary>
    public bool HasContactData(string text) => CleanContact(text).Removed;

    private (string Text, bool Removed) CleanContact(string text)
    {
        var sentences = System.Text.RegularExpressions.Regex.Split(text.Trim(), @"(?<=[.!?¡¿\n])\s+");
        var kept = new List<string>();
        var removed = false;
        foreach (var sentence in sentences)
        {
            if (string.IsNullOrWhiteSpace(sentence)) continue;
            var (_, flagged) = _questions.Sanitize(sentence);
            if (flagged) removed = true;
            else kept.Add(sentence.Trim());
        }
        return (string.Join(" ", kept), removed);
    }

    public static string ClaimUrl(OrderClaim claim) => $"https://www.mercadolibre.com.mx/ventas/{claim.ResourceId}/detalle";

    // ─────────────────────────────────────────────────────────────

    private async Task<JsonDocument?> GetJsonAsync(string token, string path, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {content[..Math.Min(300, content.Length)]}");
        return JsonDocument.Parse(content);
    }

    private async Task<(bool Ok, string Content)> SendJsonAsync(string token, HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(method, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            _logger.LogInformation("Post-sale {Method} {Path} rejected: {Body}", method, path, content);
        return (response.IsSuccessStatusCode, content);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.ToString() : null
            : null;

    private static DateTime? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
}

internal static class PostSaleStringExtensions
{
    public static string Truncate(this string text, int max) => text.Length <= max ? text : text[..max];
}
