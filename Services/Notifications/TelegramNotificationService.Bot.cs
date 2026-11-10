using Hangfire;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MarketplaceSync.Web.Models;
using MarketplaceSync.Web.Models.Notifications;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Notifications;

/// <summary>
/// Comandos del bot que no son avisos:
/// /ventas (cuánto vendiste y qué se vende más) e importar un producto mandando su link.
/// </summary>
public sealed partial class TelegramNotificationService
{
    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"(?<![\w./-])(?:us\$|\$|usd\s*)?\s*(\d{1,5}(?:[.,]\d{1,2})?)\s*(?:usd|dls|dlls|d[oó]lares)?(?![\w/])", RegexOptions.IgnoreCase)]
    private static partial Regex PriceRegex();

    private static string? FindLink(string text)
    {
        var match = LinkRegex().Match(text);
        return match.Success ? match.Value.TrimEnd('.', ',', ')', ']') : null;
    }

    /// <summary>"https://… 38.50" → 38.50 (precio en dólares escrito junto al link).</summary>
    private static decimal? FindPriceUsd(string text, string link)
    {
        var rest = text.Replace(link, " ", StringComparison.Ordinal);
        foreach (Match match in PriceRegex().Matches(rest))
        {
            var raw = match.Groups[1].Value.Replace(',', '.');
            if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value is > 0 and < 20000)
                return Math.Round(value, 2);
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────
    // /ventas
    // ─────────────────────────────────────────────────────────────

    private async Task SendSalesAsync(string token, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var org = settings.OrganizationId;
        var today = DateOnly.FromDateTime(LocalNow);
        DateTime Utc(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue).AddHours(7), DateTimeKind.Utc);

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var weekStart = today.AddDays(-6);
        var from = Utc(monthStart < weekStart ? monthStart : weekStart);

        var orders = await _context.SalesOrders.AsNoTracking()
            .Where(o => o.OrganizationId == org && o.Status == "paid" && o.DateCreated >= from)
            .Select(o => new
            {
                o.DateCreated,
                o.TotalAmount,
                o.RealProfitMxn,
                Items = o.Items.Select(i => new { i.Title, i.ExternalItemId, i.Quantity, i.UnitPrice, i.SaleFee })
            })
            .ToListAsync(cancellationToken);

        (int Count, decimal Total, int Units) Range(DateOnly start, DateOnly endExclusive)
        {
            var s = Utc(start);
            var e = Utc(endExclusive);
            var list = orders.Where(o => o.DateCreated >= s && o.DateCreated < e).ToList();
            return (list.Count, list.Sum(o => o.TotalAmount), list.Sum(o => o.Items.Sum(i => i.Quantity)));
        }

        string Line(string label, (int Count, decimal Total, int Units) r) =>
            r.Count == 0
                ? $"{label}: sin ventas"
                : $"{label}: <b>{E(r.Total.ToString("C0", Mx))}</b> · {r.Count} {(r.Count == 1 ? "venta" : "ventas")}, {r.Units} {(r.Units == 1 ? "pieza" : "piezas")}";

        var tomorrow = today.AddDays(1);
        var html = new StringBuilder();
        html.Append("💰 <b>Tus ventas</b>\n");
        html.Append(Line("Hoy", Range(today, tomorrow))).Append('\n');
        html.Append(Line("Ayer", Range(today.AddDays(-1), today))).Append('\n');
        html.Append(Line("Últimos 7 días", Range(weekStart, tomorrow))).Append('\n');
        html.Append(Line(Mx.TextInfo.ToTitleCase(today.ToString("MMMM", Mx)), Range(monthStart, tomorrow))).Append('\n');

        // Comisiones de Mercado Libre del mes (cuando vienen en el pedido).
        var monthUtc = Utc(monthStart);
        var monthOrders = orders.Where(o => o.DateCreated >= monthUtc).ToList();
        var withProfit = monthOrders.Where(o => o.RealProfitMxn.HasValue).ToList();
        if (withProfit.Count > 0)
        {
            var monthProfit = withProfit.Sum(o => o.RealProfitMxn!.Value);
            html.Append($"\n{(monthProfit < 0 ? "🔴 Pérdida" : "🟢 Ganancia")} del mes: <b>{E(Math.Abs(monthProfit).ToString("C0", Mx))}</b>")
                .Append(withProfit.Count < monthOrders.Count ? $" ({withProfit.Count} de {monthOrders.Count} ventas con costo)" : "")
                .Append("\n<i>Con el envío que cobró Mercado Libre, comisión y retención.</i>\n");
            var finance = _services.GetRequiredService<MarketplaceSync.Services.Finance.FinanceReportService>();
            var month = await finance.MonthAsync(org, today.Year, today.Month, cancellationToken);
            if (month.ExpensesTotal > 0)
                html.Append($"🧾 Gastos del mes: {E(month.ExpensesTotal.ToString("C0", Mx))} · <b>Ganancia neta: {E(month.NetProfit.ToString("C0", Mx))}</b>\n");
            var losses = withProfit.Count(o => o.RealProfitMxn < 0);
            if (losses > 0)
                html.Append($"⚠️ {losses} {(losses == 1 ? "venta dejó" : "ventas dejaron")} pérdida este mes.\n");
        }
        var monthItems = monthOrders.SelectMany(o => o.Items).ToList();
        var fees = monthItems.Where(i => i.SaleFee.HasValue).Sum(i => i.SaleFee!.Value * i.Quantity);
        if (fees > 0)
            html.Append("Comisiones de ML este mes: ").Append(E(fees.ToString("C0", Mx))).Append('\n');

        var top = orders.Where(o => o.DateCreated >= Utc(weekStart))
            .SelectMany(o => o.Items)
            .GroupBy(i => i.ExternalItemId)
            .Select(g => new { Title = g.Select(i => i.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? g.Key, Units = g.Sum(i => i.Quantity), Total = g.Sum(i => i.UnitPrice * i.Quantity) })
            .OrderByDescending(x => x.Units).ThenByDescending(x => x.Total)
            .Take(5)
            .ToList();
        if (top.Count > 0)
        {
            html.Append("\n🏆 <b>Lo más vendido (7 días)</b>\n");
            foreach (var t in top)
                html.Append(t.Units).Append("× ").Append(E(Shorten(t.Title, 48))).Append(" — ").Append(E(t.Total.ToString("C0", Mx))).Append('\n');
        }

        var buttons = settings.PublicBaseUrl is null
            ? null
            : new List<IReadOnlyList<TelegramButton>> { new[] { new TelegramButton("Ver pedidos", Url: AppUrl(settings, "/Orders")) } };
        await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value, html.ToString().TrimEnd(), buttons, cancellationToken: cancellationToken);
    }

    // ─────────────────────────────────────────────────────────────
    // Importar un producto mandando el link
    // ─────────────────────────────────────────────────────────────

    private async Task ImportFromLinkAsync(
        string token, NotificationSettings settings, string link, string text, long? userMessageId, CancellationToken cancellationToken)
    {
        var chatId = settings.TelegramChatId!.Value;
        var org = settings.OrganizationId;

        if (link.Contains("mercadolibre.", StringComparison.OrdinalIgnoreCase) || link.Contains("mercadolivre.", StringComparison.OrdinalIgnoreCase))
        {
            await _telegram.SendMessageAsync(token, chatId,
                "Ese link es de Mercado Libre. Mándame el link de la tienda donde lo compras (Amazon, eBay, Walmart…) y lo importo con su costo en dólares.",
                replyToMessageId: userMessageId, cancellationToken: cancellationToken);
            return;
        }

        var cleanLink = MarketplaceSync.Web.Helpers.UrlCleaner.CleanSourceUrl(link);
        var existing = await _context.Products.AsNoTracking()
            .Where(p => p.OrganizationId == org && (p.SourceUrl == link || p.SourceUrl == cleanLink))
            .Select(p => new { p.Id, p.Title })
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            await _telegram.SendMessageAsync(token, chatId,
                $"Ya tienes ese producto: <b>{E(existing.Title)}</b>.",
                ProductButtons(settings, existing.Id, publish: false), replyToMessageId: userMessageId, cancellationToken: cancellationToken);
            return;
        }

        var userId = await _context.OrganizationMemberships.AsNoTracking()
            .Where(m => m.OrganizationId == org && m.IsActive)
            .OrderBy(m => m.Role == OrganizationRoles.Owner ? 0 : 1).ThenBy(m => m.CreatedAt)
            .Select(m => m.UserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (userId is null)
        {
            await _telegram.SendMessageAsync(token, chatId, "No encontré un usuario activo en tu cuenta para guardar el producto.", replyToMessageId: userMessageId, cancellationToken: cancellationToken);
            return;
        }

        var progressId = await _telegram.SendMessageAsync(token, chatId, "⏳ Importando el producto…", replyToMessageId: userMessageId, cancellationToken: cancellationToken);

        var manualPrice = FindPriceUsd(text, link);
        Product product;
        try
        {
            var extracted = await _extractor.ExtractAsync(link);
            product = new Product
            {
                OrganizationId = org,
                CreatedByUserId = userId,
                SourceUrl = extracted.SourceUrl ?? link,
                SourceMarketplace = extracted.SourceMarketplace ?? "UNKNOWN",
                SourceProductId = extracted.SourceProductId,
                SourceGtin = extracted.Gtin,
                SourceGtinOrigin = extracted.Gtin is null ? null : extracted.SourceMarketplace,
                SourceGtinCheckedAt = MarketplaceSync.Services.Products.SourceBarcodeService.WasRead(extracted) ? DateTime.UtcNow : null,
                Title = await _titleTranslator.ToSpanishAsync(extracted.Title, cancellationToken),
                Description = extracted.Description,
                SourcePrice = extracted.SourcePrice,
                SourceCurrency = extracted.SourceCurrency,
                SourceStock = extracted.SourceStock,
                ImageUrl = extracted.ImageUrl,
                Brand = extracted.Brand,
                Model = extracted.Model,
                SourceStatus = extracted.SourceStatus,
                LastSourceCheckAt = DateTime.UtcNow,
                Status = "Draft",
                CreatedAt = DateTime.UtcNow
            };
            if (manualPrice is > 0)
            {
                product.SourcePrice = manualPrice;
                product.SourceCurrency = "USD";
            }
            _context.Products.Add(product);
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Telegram import failed for {Link}.", link);
            await _telegram.EditMessageAsync(token, chatId, progressId,
                "⚠️ No pude importar ese link. Revisa que sea la página de un producto (no una búsqueda) o impórtalo desde la app en Productos → Importar con IA.",
                null, cancellationToken);
            return;
        }

        await LogAsync(org, $"import:{product.Id}", NotificationKind.Import, product.Id.ToString(CultureInfo.InvariantCulture), chatId, progressId, cancellationToken);

        var html = new StringBuilder();
        html.Append("📥 <b>Importado como borrador</b>\n");
        html.Append("<b>").Append(E(product.Title ?? "Producto sin título")).Append("</b>\n");
        html.Append(E(product.SourceMarketplace)).Append(" · ")
            .Append(product.SourcePrice is > 0 ? $"US$ {product.SourcePrice.Value.ToString("0.00", CultureInfo.InvariantCulture)}" : "sin precio").Append('\n');
        var missing = MissingSourceData(product);
        if (missing.Count > 0)
            html.Append("⚠️ La tienda no dejó leer ").Append(string.Join(" ni ", missing))
                .Append(product.SourcePrice is > 0 ? "." : ". Mándame el link otra vez con el precio: <code>link 38.50</code>.").Append('\n');
        html.Append("\n✨ La IA está preparando título, categoría, atributos y precio para Mercado Libre…");

        await _telegram.EditMessageAsync(token, chatId, progressId, html.ToString(), ProductButtons(settings, product.Id, publish: false), cancellationToken);

        // La IA tarda: se hace aparte para no detener los demás mensajes del bot.
        _jobs.Enqueue<TelegramNotificationService>(s => s.PrepareImportedProductAsync(org, product.Id, chatId, progressId, CancellationToken.None));
    }

    /// <summary>Trabajo en segundo plano: prepara con IA el producto importado desde Telegram y actualiza su mensaje.</summary>
    public async Task PrepareImportedProductAsync(Guid organizationId, int productId, long chatId, long messageId, CancellationToken cancellationToken)
    {
        var settings = await GetOrCreateSettingsAsync(organizationId, cancellationToken);
        var token = TokenFor(settings);
        if (token is null || settings.TelegramChatId != chatId)
            return;

        MarketplaceSync.Services.Ai.AutopilotResult? result = null;
        try
        {
            result = await _autopilot.RunAsync(organizationId, productId, publishIfReady: false, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Autopilot failed for Telegram import {ProductId}.", productId);
        }

        await _telegram.EditMessageAsync(token, chatId, messageId,
            await ProductCardAsync(organizationId, productId, result, published: false, cancellationToken),
            ProductButtons(settings, productId, publish: result?.Ready == true), cancellationToken);
    }

    private async Task PublishFromTelegramAsync(string token, NotificationSettings settings, int productId, TelegramIncoming update, CancellationToken cancellationToken)
    {
        var exists = await _context.Products.AnyAsync(p => p.Id == productId && p.OrganizationId == settings.OrganizationId, cancellationToken);
        if (!exists)
        {
            await _telegram.AnswerCallbackAsync(token, update.CallbackId!, "Ya no encontré ese producto.", cancellationToken);
            return;
        }

        await _telegram.AnswerCallbackAsync(token, update.CallbackId!, "Publicando en Mercado Libre…", cancellationToken);
        MarketplaceSync.Services.Ai.AutopilotResult? result = null;
        string? failure = null;
        try
        {
            result = await _autopilot.RunAsync(settings.OrganizationId, productId, publishIfReady: true, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Publish from Telegram failed for {ProductId}.", productId);
            failure = "⚠️ No se pudo publicar: Mercado Libre no respondió. Pulsa Publicar otra vez en un momento o publícalo desde la app.";
        }

        if (update.CallbackMessageId is { } messageId)
            await _telegram.EditMessageAsync(token, update.ChatId, messageId,
                await ProductCardAsync(settings.OrganizationId, productId, result, published: result?.Published == true, cancellationToken, failure),
                result?.Published == true ? null : ProductButtons(settings, productId, publish: result?.Ready == true || failure is not null),
                cancellationToken);
    }

    private async Task<string> ProductCardAsync(Guid org, int productId, MarketplaceSync.Services.Ai.AutopilotResult? result, bool published, CancellationToken cancellationToken, string? failure = null)
    {
        var product = await _context.Products.AsNoTracking()
            .Where(p => p.Id == productId && p.OrganizationId == org)
            .Select(p => new
            {
                p.Title,
                p.SourceMarketplace,
                p.SourcePrice,
                Price = p.MarketplacePublications.Where(x => x.Marketplace == "MercadoLibre").Select(x => (decimal?)x.Price).FirstOrDefault()
            })
            .FirstOrDefaultAsync(cancellationToken);

        var html = new StringBuilder();
        if (result is null)
            html.Append("📥 <b>Importado como borrador</b>\n");
        else if (published)
            html.Append("🚀 <b>Publicado en Mercado Libre</b>\n");
        else if (result.Ready)
            html.Append("✅ <b>Listo para publicar</b>\n");
        else
            html.Append("📝 <b>Borrador preparado</b>\n");

        html.Append("<b>").Append(E(product?.Title ?? "Producto")).Append("</b>\n");
        html.Append("Costo: ").Append(product?.SourcePrice is > 0 ? $"US$ {product.SourcePrice.Value.ToString("0.00", CultureInfo.InvariantCulture)}" : "sin precio")
            .Append(" · ").Append(E(product?.SourceMarketplace));
        if (product?.Price is > 0)
            html.Append("\nPrecio en ML: <b>").Append(E(product.Price.Value.ToString("C0", Mx))).Append("</b>");
        html.Append('\n');

        if (failure is not null)
        {
            html.Append('\n').Append(E(failure));
        }
        else if (result is null)
        {
            html.Append("\n⚠️ La IA no pudo prepararlo ahora. Ábrelo en la app y pulsa “Completar con IA”.");
        }
        else
        {
            if (published && result.Permalink is not null)
                html.Append("\n<a href=\"").Append(E(result.Permalink)).Append("\">Ver la publicación</a>\n");
            if (result.Missing.Count > 0)
                html.Append("\n<b>Falta:</b> ").Append(E(string.Join(" · ", result.Missing))).Append('\n');
            if (!result.AiConfigured)
                html.Append("\nℹ️ La IA no está configurada (Anthropic__ApiKey en Render): solo se calculó lo básico.\n");
            if (result.Ready && !published)
                html.Append("\nRevisa y pulsa <b>Publicar</b>, o ábrelo en la app para ajustarlo.");
            else if (!result.Ready && !published)
                html.Append("\nCompleta lo que falta en la app. Cuando registres la mercancía como recibida, se publica sola si así lo tienes configurado.");
        }
        return html.ToString().TrimEnd();
    }

    private static List<IReadOnlyList<TelegramButton>>? ProductButtons(NotificationSettings settings, int productId, bool publish)
    {
        var row = new List<TelegramButton>();
        if (publish)
            row.Add(new TelegramButton("🚀 Publicar", CallbackData: $"pb:{productId}"));
        if (settings.PublicBaseUrl is not null)
            row.Add(new TelegramButton("Abrir en la app", Url: AppUrl(settings, $"/Products/PublishToMercadoLibre/{productId}")));
        return row.Count == 0 ? null : new List<IReadOnlyList<TelegramButton>> { row };
    }

    private static List<string> MissingSourceData(Product product)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(product.ImageUrl)) missing.Add("la foto");
        if (product.SourcePrice is not > 0) missing.Add("el precio");
        return missing;
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    // ─────────────────────────────────────────────────────────────
    // Avisos de envíos atrasados y reputación
    // ─────────────────────────────────────────────────────────────

    /// <summary>Un aviso por pedido cuando pasa su hora límite de despacho.</summary>
    private async Task NotifyLateOrdersAsync(string token, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var org = settings.OrganizationId;
        var sent = _context.NotificationLogs.Where(l => l.OrganizationId == org && l.Kind == NotificationKind.LateOrder).Select(l => l.Key);
        var late = await MarketplaceSync.Web.Models.Orders.SalesOrderShippingQueries
            .LateToShip(_context.SalesOrders.AsNoTracking().Where(o => o.OrganizationId == org), DateTime.UtcNow)
            .Where(o => !sent.Contains("late:" + o.Id))
            .Include(o => o.Items)
            .OrderBy(o => o.ShippingDeadline)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var order in late)
        {
            var items = string.Join(", ", order.Items.Take(3).Select(i => $"{i.Quantity}× {E(Shorten(i.Title ?? i.ExternalItemId, 50))}"));
            var html = $"🚨 <b>Pedido atrasado</b> · #{E(order.ExternalOrderId)}\n{items}\n" +
                       $"La hora límite era {order.ShippingDeadline!.Value.AddHours(-7):dd/MM HH:mm}. Despáchalo hoy: cada día cuenta como demora en tu reputación.";
            var buttons = settings.PublicBaseUrl is null
                ? null
                : new List<IReadOnlyList<TelegramButton>> { new[] { new TelegramButton("Ver por enviar", Url: AppUrl(settings, "/Orders?view=late")) } };
            var messageId = await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value, html, buttons, cancellationToken: cancellationToken);
            await LogAsync(org, "late:" + order.Id, NotificationKind.LateOrder, order.Id.ToString(CultureInfo.InvariantCulture), settings.TelegramChatId, messageId, cancellationToken);
        }
    }

    /// <summary>
    /// Reputación: avisa una vez al día si una métrica pasó el límite verde,
    /// y una vez por semana si está cerca (80% o más del límite).
    /// </summary>
    private async Task NotifyReputationAsync(string token, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var org = settings.OrganizationId;
        IReadOnlyList<MarketplaceSync.Services.Metrics.ReputationAlert> alerts;
        try
        {
            alerts = await _metrics.GetReputationAlertsAsync(org, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Reputation alerts unavailable for Telegram.");
            return;
        }

        var local = LocalNow;
        var week = ISOWeek.GetWeekOfYear(local);
        foreach (var alert in alerts)
        {
            var key = alert.Risk == "over"
                ? $"rep:{alert.Account}:{alert.Metric}:over:{local:yyyyMMdd}"
                : $"rep:{alert.Account}:{alert.Metric}:near:{local:yyyy}-{week}";
            if (key.Length > 190) key = key[..190];
            if (await _context.NotificationLogs.AnyAsync(l => l.OrganizationId == org && l.Key == key, cancellationToken))
                continue;

            var rate = alert.RatePercent.ToString("0.##", Mx) + "%";
            var limit = alert.GreenLimit.ToString("0.#", Mx) + "%";
            var html = alert.Risk == "over"
                ? $"🔴 <b>Reputación en riesgo</b> · {E(alert.Account)}\n<b>{E(alert.Metric)}: {rate}</b>, arriba del límite verde ({limit}).\nMercado Libre puede bajarte de color y mostrar menos tus publicaciones. Evita nuevos casos esta semana."
                : $"🟡 <b>Cuida tu reputación</b> · {E(alert.Account)}\n{E(alert.Metric)}: {rate}, cerca del límite verde ({limit})." +
                  (alert.RoomLeft is { } room ? room == 0 ? "\nUn caso más y pasas el límite." : $"\nTe {(room == 1 ? "queda 1 caso" : $"quedan {room} casos")} antes de pasarlo (con tus ventas de hoy)." : "");
            var buttons = settings.PublicBaseUrl is null
                ? null
                : new List<IReadOnlyList<TelegramButton>> { new[] { new TelegramButton("Ver métricas", Url: AppUrl(settings, "/Metrics")) } };
            var messageId = await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value, html, buttons, cancellationToken: cancellationToken);
            await LogAsync(org, key, NotificationKind.Reputation, null, settings.TelegramChatId, messageId, cancellationToken);
        }
    }

    /// <summary>Escapa texto para mensajes HTML de Telegram (solo &amp; &lt; &gt;, conserva acentos).</summary>
    public static string Escape(string? text) => E(text);

    /// <summary>Manda un reporte (agente, tareas automáticas) al chat vinculado, con un botón a la app si hay URL pública.</summary>
    public async Task SendReportAsync(Guid organizationId, string html, string buttonText, string path, CancellationToken cancellationToken = default)
    {
        var settings = await _context.NotificationSettings.AsNoTracking().FirstOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        var token = settings is null ? null : TokenFor(settings);
        if (settings?.TelegramChatId is not { } chatId || token is null)
            return;
        var buttons = settings.PublicBaseUrl is null
            ? null
            : new List<IReadOnlyList<TelegramButton>> { new[] { new TelegramButton(buttonText, Url: AppUrl(settings, path)) } };
        await _telegram.SendMessageAsync(token, chatId, html, buttons, cancellationToken: cancellationToken);
    }

    // ─────────────────────────────────────────────────────────────
    // Postventa: mensajes de compradores y reclamos
    // ─────────────────────────────────────────────────────────────

    private async Task NotifyBuyerMessagesAsync(string token, NotificationSettings settings, DateTime since, CancellationToken cancellationToken)
    {
        var org = settings.OrganizationId;
        var sent = _context.NotificationLogs.Where(l => l.OrganizationId == org && l.Kind == NotificationKind.BuyerMessage).Select(l => l.Key);
        var messages = await _context.OrderMessages.AsNoTracking()
            .Where(m => m.OrganizationId == org && !m.FromSeller && m.SentAt >= since && m.SalesOrderId != null && !sent.Contains("msg:" + m.ExternalId))
            .OrderBy(m => m.SentAt)
            .Take(10)
            .ToListAsync(cancellationToken);
        foreach (var m in messages)
        {
            var order = await _context.SalesOrders.AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == m.SalesOrderId, cancellationToken);
            var html = $"💬 <b>Mensaje de {E(order?.BuyerNickname ?? "un comprador")}</b>\n" +
                       (order?.Items.FirstOrDefault()?.Title is { } title ? $"<b>{E(Shorten(title, 70))}</b>\n" : "") +
                       $"«{E(Shorten(m.Text, 600))}»\n\n<i>Responde a este mensaje para contestarle.</i>";
            var buttons = settings.PublicBaseUrl is null
                ? null
                : new List<IReadOnlyList<TelegramButton>> { new[] { new TelegramButton("Abrir conversación", Url: AppUrl(settings, $"/Orders/Details/{m.SalesOrderId}#conversacion")) } };
            var messageId = await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value, html, buttons, cancellationToken: cancellationToken);
            await LogAsync(org, "msg:" + m.ExternalId, NotificationKind.BuyerMessage, m.SalesOrderId!.Value.ToString(CultureInfo.InvariantCulture), settings.TelegramChatId, messageId, cancellationToken);
        }
    }

    /// <summary>Un aviso cuando se abre un reclamo y otro si pasa a disputa (interviene Mercado Libre).</summary>
    private async Task NotifyClaimsAsync(string token, NotificationSettings settings, DateTime since, CancellationToken cancellationToken)
    {
        var org = settings.OrganizationId;
        var claims = await _context.OrderClaims.AsNoTracking()
            .Where(c => c.OrganizationId == org && c.Status == "opened" && c.LastUpdated >= since.AddDays(-2))
            .OrderBy(c => c.DueDate ?? c.DateCreated)
            .Take(10)
            .ToListAsync(cancellationToken);
        foreach (var c in claims)
        {
            var key = $"claim:{c.ExternalId}:{c.Stage ?? "claim"}";
            if (await _context.NotificationLogs.AnyAsync(l => l.OrganizationId == org && l.Key == key, cancellationToken))
                continue;
            var title = c.SalesOrderId is { } oid
                ? await _context.SalesOrderItems.AsNoTracking().Where(i => i.SalesOrderId == oid).Select(i => i.Title).FirstOrDefaultAsync(cancellationToken)
                : null;
            var due = c.DueDate is { } d ? $"\n⏰ Responde antes del <b>{d.AddHours(-7):dd/MM HH:mm}</b> para no afectar tu reputación." : "";
            var html = (c.Stage == "dispute"
                           ? $"🔴 <b>El reclamo pasó a disputa</b>: {E(c.ReasonLabel)}\nMercado Libre va a decidir; manda tus pruebas al mediador."
                           : $"⚠️ <b>Reclamo abierto</b>: {E(c.ReasonLabel)}") +
                       (title is null ? $"\nPedido {E(c.ResourceId)}" : $"\n{E(Shorten(title, 70))}") + due;
            var buttons = settings.PublicBaseUrl is null
                ? null
                : new List<IReadOnlyList<TelegramButton>> { new[] { new TelegramButton("Atender reclamo", Url: AppUrl(settings, $"/PostSale/Claim/{c.Id}")) } };
            var messageId = await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value, html, buttons, cancellationToken: cancellationToken);
            await LogAsync(org, key, NotificationKind.Claim, c.Id.ToString(CultureInfo.InvariantCulture), settings.TelegramChatId, messageId, cancellationToken);
        }
    }

    private async Task ReplyToBuyerAsync(string token, NotificationSettings settings, int orderId, string text, long? userMessageId, CancellationToken cancellationToken)
    {
        string? error;
        try
        {
            error = await _postSale.SendMessageAsync(settings.OrganizationId, orderId, text, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Reply to buyer from Telegram failed for order {Order}.", orderId);
            error = "Mercado Libre no respondió; intenta de nuevo en un momento.";
        }
        await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value,
            error is null ? "✅ Mensaje enviado al comprador." : "⚠️ " + E(error),
            replyToMessageId: userMessageId, cancellationToken: cancellationToken);
    }
}
