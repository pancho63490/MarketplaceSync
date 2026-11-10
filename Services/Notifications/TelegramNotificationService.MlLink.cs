using Hangfire;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Models.Notifications;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Notifications;

/// <summary>Link de una publicación de Mercado Libre por Telegram: se lee con la API oficial y el agente busca la misma pieza en eBay.</summary>
public sealed partial class TelegramNotificationService
{
    [GeneratedRegex(@"/p/(ML[A-Z]\d{5,})", RegexOptions.IgnoreCase)]
    private static partial Regex CatalogProductRegex();

    [GeneratedRegex(@"(ML[A-Z])-?(\d{6,})", RegexOptions.IgnoreCase)]
    private static partial Regex MercadoLibreItemRegex();

    private sealed record MlListing(string Title, decimal? Price, int? Sold, string? SellerId, string? Brand, string? PartNumber, string? Model, string? ItemId);

    private string MlApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : "https://api.mercadolibre.com";

    private async Task HandleMercadoLibreLinkAsync(string token, NotificationSettings settings, string link, long? userMessageId, CancellationToken cancellationToken)
    {
        var chatId = settings.TelegramChatId!.Value;
        var org = settings.OrganizationId;

        var account = await _context.MarketplaceAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == org && a.Marketplace == "MercadoLibre" && a.Status == "Connected")
            .OrderBy(a => a.Id).FirstOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            await _telegram.SendMessageAsync(token, chatId, "Para leer ese link necesito tu cuenta de Mercado Libre conectada.", replyToMessageId: userMessageId, cancellationToken: cancellationToken);
            return;
        }

        MlListing? listing;
        try
        {
            var accessToken = await Service<IMercadoLibreAuthService>().GetValidAccessTokenAsync(org, account.Id);
            listing = await ReadMercadoLibreLinkAsync(link, accessToken, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Could not read Mercado Libre link {Link}.", link);
            listing = null;
        }
        if (listing is null)
        {
            await _telegram.SendMessageAsync(token, chatId,
                "No pude leer esa publicación de Mercado Libre. Mándame el link de la publicación (el que tiene MLM-… o /p/MLM…), o escríbeme qué buscar: <code>/encargo faros Ranger 2019</code>.",
                replyToMessageId: userMessageId, cancellationToken: cancellationToken);
            return;
        }

        // ¿Es una publicación tuya?
        var sellerIds = await _context.MarketplaceAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == org && a.Marketplace == "MercadoLibre")
            .Select(a => a.ExternalAccountId).ToListAsync(cancellationToken);
        if (listing.SellerId is not null && sellerIds.Contains(listing.SellerId))
        {
            var mine = listing.ItemId is null ? null : await _context.MarketplacePublications.AsNoTracking()
                .Where(p => p.Product.OrganizationId == org && p.ExternalItemId == listing.ItemId)
                .Select(p => (int?)p.Id).FirstOrDefaultAsync(cancellationToken);
            await _telegram.SendMessageAsync(token, chatId, $"Esa publicación es tuya: <b>{E(listing.Title)}</b>.",
                mine is { } pubId && settings.PublicBaseUrl is not null ? new[] { new[] { new TelegramButton("Editar", Url: AppUrl(settings, $"/Publications/Edit/{pubId}")) } } : null,
                replyToMessageId: userMessageId, cancellationToken: cancellationToken);
            return;
        }

        if (!Service<MarketplaceSync.Services.Ai.ClaudeClient>().IsConfigured)
        {
            await _telegram.SendMessageAsync(token, chatId,
                $"Es «{E(listing.Title)}». Para buscarla en eBay necesito la IA: agrega <code>Anthropic__ApiKey</code> en Render.",
                replyToMessageId: userMessageId, cancellationToken: cancellationToken);
            return;
        }

        var details = new List<string>();
        if (listing.Brand is not null) details.Add("marca " + listing.Brand);
        if (listing.PartNumber is not null) details.Add("número de parte " + listing.PartNumber);
        else if (listing.Model is not null) details.Add("modelo " + listing.Model);
        var market = listing.Price is > 0 ? $"se vende a {Money(listing.Price.Value)} en Mercado Libre" : "se vende en Mercado Libre";
        if (listing.Sold is > 0) market += $" con {listing.Sold} vendidos";
        var request = $"Busca en eBay la misma pieza que «{listing.Title}»" +
                      (details.Count > 0 ? $" ({string.Join(", ", details)})" : "") +
                      $"; {market}. Impórtala si deja ganancia (no uses fotos del otro vendedor).";
        if (request.Length > 500) request = request[..500];
        _jobs.Enqueue<MarketplaceSync.Services.Jobs.MarketplaceJobsService>(job => job.RunImportAgentRequest(org, request));

        var html = $"🔎 <b>Lo busco en eBay</b>\n{E(listing.Title)}\n" +
                   (listing.Price is > 0 ? $"En Mercado Libre: {Money(listing.Price.Value)}" + (listing.Sold is > 0 ? $" · {listing.Sold} vendidos" : "") + "\n" : "") +
                   (details.Count > 0 ? E(string.Join(" · ", details)) + "\n" : "") +
                   "El agente busca la misma pieza, calcula tu ganancia con el cruce y la importa si conviene. Te aviso aquí.";
        await _telegram.SendMessageAsync(token, chatId, html, replyToMessageId: userMessageId, cancellationToken: cancellationToken);
    }

    /// <summary>Lee la publicación (/items/{id}) o la ficha de catálogo (/products/{id}) con la API oficial.</summary>
    private async Task<MlListing?> ReadMercadoLibreLinkAsync(string link, string accessToken, CancellationToken cancellationToken)
    {
        var client = Service<IHttpClientFactory>().CreateClient();
        async Task<JsonDocument?> GetAsync(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, MlApiBase + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }

        // Ficha de catálogo: https://www.mercadolibre.com.mx/…/p/MLM12345678
        if (CatalogProductRegex().Match(link) is { Success: true } catalog)
        {
            var productId = catalog.Groups[1].Value.ToUpperInvariant();
            using var product = await GetAsync($"/products/{Uri.EscapeDataString(productId)}");
            if (product is not null)
            {
                var root = product.RootElement;
                var name = JStr(root, "name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var winner = root.TryGetProperty("buy_box_winner", out var w) && w.ValueKind == JsonValueKind.Object ? w : default;
                    return new MlListing(name!, JDec(winner, "price"), null, JStr(winner, "seller_id"),
                        Attribute(root, "BRAND"), Attribute(root, "PART_NUMBER") ?? Attribute(root, "MPN"), Attribute(root, "MODEL"), JStr(winner, "item_id"));
                }
            }
        }

        // Publicación: https://articulo.mercadolibre.com.mx/MLM-123456789-…
        if (MercadoLibreItemRegex().Match(link) is not { Success: true } item) return null;
        var itemId = item.Groups[1].Value.ToUpperInvariant() + item.Groups[2].Value;
        using var document = await GetAsync($"/items/{Uri.EscapeDataString(itemId)}");
        if (document is null) return null;
        var itemRoot = document.RootElement;
        var title = JStr(itemRoot, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;
        return new MlListing(title!, JDec(itemRoot, "price"), itemRoot.TryGetProperty("sold_quantity", out var s) && s.TryGetInt32(out var sold) ? sold : null,
            JStr(itemRoot, "seller_id"), Attribute(itemRoot, "BRAND"), Attribute(itemRoot, "PART_NUMBER") ?? Attribute(itemRoot, "MPN"), Attribute(itemRoot, "MODEL"), itemId);
    }

    private static string? Attribute(JsonElement root, string id)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in attributes.EnumerateArray())
            if (JStr(a, "id") == id && JStr(a, "value_name") is { Length: > 0 and <= 80 } value)
                return value;
        return null;
    }

    private static string? JStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;

    private static decimal? JDec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;
}
