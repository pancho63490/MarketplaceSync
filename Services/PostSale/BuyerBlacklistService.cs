using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.PostSale;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.PostSale;

public sealed record BlockedBuyerRow(BlockedBuyer Buyer, string AccountName, int Orders, decimal Spent, int Claims);

/// <summary>
/// Lista negra de compradores en Mercado Libre: bloquear para comprar (/users/{seller}/order_blacklist)
/// y para preguntar (/users/{seller}/questions_blacklist). Se guarda también aquí para mostrar quién y por qué.
/// </summary>
public sealed class BuyerBlacklistService
{
    private readonly AppDbContext _context;
    private readonly IMercadoLibreAuthService _auth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BuyerBlacklistService> _logger;

    public BuyerBlacklistService(AppDbContext context, IMercadoLibreAuthService auth, IHttpClientFactory httpClientFactory,
        IConfiguration configuration, ILogger<BuyerBlacklistService> logger)
    {
        _context = context;
        _auth = auth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private string ApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } v ? v.TrimEnd('/') : "https://api.mercadolibre.com";

    public static readonly string[] Reasons =
    {
        "Reclamo injusto o abuso de devoluciones",
        "Compra y cancela seguido",
        "Pidió contacto o pago fuera de Mercado Libre",
        "Groserías o amenazas",
        "Revendedor que solo pregunta precios"
    };

    public Task<BlockedBuyer?> FindAsync(Guid org, int accountId, string? buyerId, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(buyerId)
            ? Task.FromResult<BlockedBuyer?>(null)
            : _context.BlockedBuyers.AsNoTracking()
                .FirstOrDefaultAsync(b => b.OrganizationId == org && b.MarketplaceAccountId == accountId && b.BuyerId == buyerId, cancellationToken);

    public async Task<IReadOnlyList<BlockedBuyerRow>> ListAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var blocked = await _context.BlockedBuyers.AsNoTracking()
            .Where(b => b.OrganizationId == org)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(cancellationToken);
        if (blocked.Count == 0)
            return Array.Empty<BlockedBuyerRow>();

        var ids = blocked.Select(b => b.BuyerId).Distinct().ToList();
        var accounts = await _context.MarketplaceAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == org)
            .ToDictionaryAsync(a => a.Id, a => a.DisplayName ?? a.ExternalAccountId, cancellationToken);
        var orders = await _context.SalesOrders.AsNoTracking()
            .Where(o => o.OrganizationId == org && o.BuyerId != null && ids.Contains(o.BuyerId))
            .Select(o => new { o.Id, o.BuyerId, o.MarketplaceAccountId, o.PaidAmount, o.Status })
            .ToListAsync(cancellationToken);
        var orderIds = orders.Select(o => o.Id).ToList();
        var claims = await _context.OrderClaims.AsNoTracking()
            .Where(c => c.OrganizationId == org && c.SalesOrderId != null && orderIds.Contains(c.SalesOrderId.Value))
            .Select(c => c.SalesOrderId!.Value)
            .ToListAsync(cancellationToken);

        return blocked.Select(b =>
        {
            var mine = orders.Where(o => o.BuyerId == b.BuyerId && o.MarketplaceAccountId == b.MarketplaceAccountId).ToList();
            return new BlockedBuyerRow(b, accounts.GetValueOrDefault(b.MarketplaceAccountId) ?? "Mercado Libre",
                mine.Count, mine.Where(o => o.Status == "paid").Sum(o => o.PaidAmount), claims.Count(c => mine.Any(o => o.Id == c)));
        }).ToList();
    }

    /// <summary>Bloquea al comprador del pedido. Devuelve null si salió bien o el motivo en palabras simples.</summary>
    public async Task<string?> BlockOrderBuyerAsync(Guid org, int orderId, string? reason, bool orders, bool questions, CancellationToken cancellationToken = default)
    {
        var order = await _context.SalesOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == org, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (string.IsNullOrWhiteSpace(order.BuyerId))
            return "Este pedido no trae el comprador; sincroniza los pedidos e intenta de nuevo.";
        return await BlockAsync(org, order.MarketplaceAccountId, order.BuyerId, order.BuyerNickname, reason, orders, questions, cancellationToken);
    }

    public async Task<string?> BlockAsync(Guid org, int accountId, string buyerId, string? nickname, string? reason,
        bool orders, bool questions, CancellationToken cancellationToken = default)
    {
        if (!orders && !questions)
            return "Elige si lo bloqueas para comprar, para preguntar o ambos.";
        var account = await _context.MarketplaceAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == accountId && a.OrganizationId == org, cancellationToken)
            ?? throw new KeyNotFoundException();
        var token = await _auth.GetValidAccessTokenAsync(org, accountId);
        var seller = Uri.EscapeDataString(account.ExternalAccountId);
        var errors = new List<string>();

        if (orders && !await PostBlockAsync(token, $"/users/{seller}/order_blacklist", buyerId, cancellationToken))
            errors.Add("comprar");
        if (questions && !await PostBlockAsync(token, $"/users/{seller}/questions_blacklist", buyerId, cancellationToken))
            errors.Add("preguntar");
        var okOrders = orders && !errors.Contains("comprar");
        var okQuestions = questions && !errors.Contains("preguntar");
        if (!okOrders && !okQuestions)
            return "Mercado Libre no aceptó el bloqueo. Intenta de nuevo en un rato o bloquéalo desde Mercado Libre.";

        var row = await _context.BlockedBuyers.FirstOrDefaultAsync(
            b => b.OrganizationId == org && b.MarketplaceAccountId == accountId && b.BuyerId == buyerId, cancellationToken);
        if (row is null)
        {
            row = new BlockedBuyer { OrganizationId = org, MarketplaceAccountId = accountId, BuyerId = buyerId, BlocksOrders = false, BlocksQuestions = false };
            _context.BlockedBuyers.Add(row);
        }
        row.Nickname = nickname ?? row.Nickname;
        row.Reason = string.IsNullOrWhiteSpace(reason) ? row.Reason : reason.Trim()[..Math.Min(300, reason.Trim().Length)];
        row.BlocksOrders |= okOrders;
        row.BlocksQuestions |= okQuestions;
        await _context.SaveChangesAsync(cancellationToken);

        return errors.Count == 0 ? null
            : $"Quedó bloqueado, pero Mercado Libre no aceptó el bloqueo para {string.Join(" ni ", errors)}.";
    }

    public async Task<string?> UnblockAsync(Guid org, long id, CancellationToken cancellationToken = default)
    {
        var row = await _context.BlockedBuyers.FirstOrDefaultAsync(b => b.Id == id && b.OrganizationId == org, cancellationToken)
                  ?? throw new KeyNotFoundException();
        var account = await _context.MarketplaceAccounts.AsNoTracking()
            .FirstAsync(a => a.Id == row.MarketplaceAccountId, cancellationToken);
        var token = await _auth.GetValidAccessTokenAsync(org, row.MarketplaceAccountId);
        var seller = Uri.EscapeDataString(account.ExternalAccountId);
        var buyer = Uri.EscapeDataString(row.BuyerId);
        var ok = true;
        if (row.BlocksOrders) ok &= await DeleteAsync(token, $"/users/{seller}/order_blacklist/{buyer}", cancellationToken);
        if (row.BlocksQuestions) ok &= await DeleteAsync(token, $"/users/{seller}/questions_blacklist/{buyer}", cancellationToken);
        if (!ok)
            return "Mercado Libre no quitó el bloqueo. Intenta de nuevo en un rato.";
        _context.BlockedBuyers.Remove(row);
        await _context.SaveChangesAsync(cancellationToken);
        return null;
    }

    /// <summary>Trae los bloqueados que ya tenías en Mercado Libre (hechos desde su página) para verlos aquí.</summary>
    public async Task<int> ImportFromMercadoLibreAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var accounts = await _context.MarketplaceAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == org && a.Marketplace == "MercadoLibre" && a.Status == "Connected")
            .ToListAsync(cancellationToken);
        var added = 0;
        foreach (var account in accounts)
        {
            try
            {
                var token = await _auth.GetValidAccessTokenAsync(org, account.Id);
                var seller = Uri.EscapeDataString(account.ExternalAccountId);
                var orderIds = await ListIdsAsync(token, $"/users/{seller}/order_blacklist", cancellationToken);
                var questionIds = await ListIdsAsync(token, $"/users/{seller}/questions_blacklist?limit=50", cancellationToken);
                var existing = await _context.BlockedBuyers
                    .Where(b => b.OrganizationId == org && b.MarketplaceAccountId == account.Id)
                    .ToDictionaryAsync(b => b.BuyerId, cancellationToken);
                foreach (var id in orderIds.Union(questionIds))
                {
                    if (!existing.TryGetValue(id, out var row))
                    {
                        var nickname = await _context.SalesOrders.AsNoTracking()
                            .Where(o => o.OrganizationId == org && o.BuyerId == id).Select(o => o.BuyerNickname).FirstOrDefaultAsync(cancellationToken);
                        row = new BlockedBuyer
                        {
                            OrganizationId = org, MarketplaceAccountId = account.Id, BuyerId = id, Nickname = nickname,
                            Reason = "Bloqueado desde Mercado Libre", BlocksOrders = false, BlocksQuestions = false
                        };
                        _context.BlockedBuyers.Add(row);
                        existing[id] = row;
                        added++;
                    }
                    row.BlocksOrders |= orderIds.Contains(id);
                    row.BlocksQuestions |= questionIds.Contains(id);
                }
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(exception, "Blacklist import failed for account {Account}.", account.Id);
            }
        }
        return added;
    }

    // ─────────────────────────────────────────────────────────────

    private async Task<bool> PostBlockAsync(string token, string path, string buyerId, CancellationToken cancellationToken)
    {
        // Preguntas documenta {"user_id": …}; para compras aceptamos también la forma {"blocked": {"id": …}}.
        long.TryParse(buyerId, out var numeric);
        object idValue = numeric > 0 ? numeric : buyerId;
        var (ok, content) = await SendAsync(token, HttpMethod.Post, path, new { user_id = idValue }, cancellationToken);
        if (!ok && path.Contains("order_blacklist") && !content.Contains("already", StringComparison.OrdinalIgnoreCase))
            (ok, content) = await SendAsync(token, HttpMethod.Post, path, new { blocked = new { id = idValue } }, cancellationToken);
        // Si ya estaba bloqueado, para nosotros es éxito.
        return ok || content.Contains("already", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> DeleteAsync(string token, string path, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Blacklist DELETE {Path} rejected: {Status}", path, (int)response.StatusCode);
            return false;
        }
        return true;
    }

    private async Task<HashSet<string>> ListIdsAsync(string token, string path, CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>();
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return ids;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        // Formas vistas: {"users":[{"id":1}]}, [{"user":{"id":1,"blocked":true}}], [{"id":1}].
        var list = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
            : doc.RootElement.TryGetProperty("users", out var u) ? u
            : doc.RootElement.TryGetProperty("results", out var r) ? r : default;
        if (list.ValueKind != JsonValueKind.Array)
            return ids;
        foreach (var e in list.EnumerateArray())
        {
            var node = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("user", out var inner) ? inner : e;
            if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("blocked", out var b) && b.ValueKind == JsonValueKind.False)
                continue;
            var id = node.ValueKind == JsonValueKind.Object && node.TryGetProperty("id", out var v) ? v.ToString()
                : node.ValueKind is JsonValueKind.Number or JsonValueKind.String ? node.ToString() : null;
            if (!string.IsNullOrWhiteSpace(id))
                ids.Add(id);
        }
        return ids;
    }

    private async Task<(bool Ok, string Content)> SendAsync(string token, HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(method, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            _logger.LogInformation("Blacklist {Path} rejected: {Body}", path, content);
        return (response.IsSuccessStatusCode, content);
    }
}
