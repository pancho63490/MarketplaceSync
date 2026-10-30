using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Marketplace;
using MarketplaceSync.Web.Models.Orders;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.MercadoLibre;

public sealed class MercadoLibreOrderSyncService : IMercadoLibreOrderSyncService
{
    private const int PageSize = 50;
    private static readonly TimeSpan InitialLookback = TimeSpan.FromDays(30);
    private static readonly TimeSpan SyncOverlap = TimeSpan.FromMinutes(10);

    private readonly AppDbContext _dbContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMercadoLibreAuthService _authService;
    private readonly ILogger<MercadoLibreOrderSyncService> _logger;

    public MercadoLibreOrderSyncService(
        AppDbContext dbContext,
        IHttpClientFactory httpClientFactory,
        IMercadoLibreAuthService authService,
        ILogger<MercadoLibreOrderSyncService> logger)
    {
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;
        _authService = authService;
        _logger = logger;
    }

    public async Task<OrderSyncSummary> SyncAllAccountsAsync(
        Guid? organizationId = null,
        CancellationToken cancellationToken = default)
    {
        var accountIds = await _dbContext.MarketplaceAccounts
            .AsNoTracking()
            .Where(x => x.Marketplace == "MercadoLibre" &&
                        x.Status == "Connected" &&
                        x.Tokens.Any(token => token.IsActive) &&
                        (!organizationId.HasValue || x.OrganizationId == organizationId.Value))
            .OrderBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        var summary = new OrderSyncSummary();

        foreach (var accountId in accountIds)
        {
            try
            {
                var result = await SyncAccountAsync(accountId, cancellationToken);
                summary.AccountsProcessed++;
                summary.OrdersCreated += result.Created;
                summary.OrdersUpdated += result.Updated;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                summary.AccountsFailed++;
                _logger.LogError(
                    exception,
                    "Could not synchronize Mercado Libre orders for account {MarketplaceAccountId}.",
                    accountId);

                var account = await _dbContext.MarketplaceAccounts
                    .FirstOrDefaultAsync(x => x.Id == accountId, cancellationToken);
                if (account != null)
                {
                    account.LastOrdersSyncError = Truncate(exception.Message, 1000);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
            }
        }

        return summary;
    }

    private async Task<(int Created, int Updated)> SyncAccountAsync(
        int accountId,
        CancellationToken cancellationToken)
    {
        var account = await _dbContext.MarketplaceAccounts
            .FirstAsync(x => x.Id == accountId, cancellationToken);
        var accessToken = await _authService.GetValidAccessTokenAsync(
            account.OrganizationId,
            account.Id);

        var syncStartedAt = DateTime.UtcNow;
        var updatedFrom = (account.LastOrdersSyncAt ?? syncStartedAt.Subtract(InitialLookback))
            .Subtract(SyncOverlap);
        var offset = 0;
        var created = 0;
        var updated = 0;
        var client = _httpClientFactory.CreateClient();

        while (true)
        {
            var url = BuildSearchUrl(account.ExternalAccountId, updatedFrom, offset);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Mercado Libre orders search returned HTTP {(int)response.StatusCode}.");
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                contentStream,
                cancellationToken: cancellationToken);
            var root = document.RootElement;
            var results = root.TryGetProperty("results", out var resultsElement) &&
                          resultsElement.ValueKind == JsonValueKind.Array
                ? resultsElement
                : default;

            if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
                break;

            var externalOrderIds = results.EnumerateArray()
                .Select(x => ReadString(x, "id"))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToArray();

            var existingOrders = await _dbContext.SalesOrders
                .Include(x => x.Items)
                .Where(x => x.MarketplaceAccountId == account.Id &&
                            externalOrderIds.Contains(x.ExternalOrderId))
                .ToDictionaryAsync(x => x.ExternalOrderId, cancellationToken);

            foreach (var orderJson in results.EnumerateArray())
            {
                var externalOrderId = ReadString(orderJson, "id");
                if (string.IsNullOrWhiteSpace(externalOrderId))
                    continue;

                if (!existingOrders.TryGetValue(externalOrderId, out var order))
                {
                    order = new SalesOrder
                    {
                        OrganizationId = account.OrganizationId,
                        MarketplaceAccountId = account.Id,
                        ExternalOrderId = externalOrderId
                    };
                    _dbContext.SalesOrders.Add(order);
                    created++;
                }
                else
                {
                    _dbContext.SalesOrderItems.RemoveRange(order.Items);
                    order.Items.Clear();
                    updated++;
                }

                MapOrder(order, orderJson);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            var total = root.TryGetProperty("paging", out var paging) &&
                        paging.TryGetProperty("total", out var totalElement) &&
                        totalElement.TryGetInt32(out var parsedTotal)
                ? parsedTotal
                : offset + results.GetArrayLength();

            offset += results.GetArrayLength();
            if (offset >= total)
                break;
        }

        account.LastOrdersSyncAt = syncStartedAt;
        account.LastOrdersSyncError = null;
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Mercado Libre order sync completed for account {MarketplaceAccountId}: {Created} created, {Updated} updated.",
            account.Id,
            created,
            updated);

        return (created, updated);
    }

    private static string BuildSearchUrl(string sellerId, DateTime updatedFrom, int offset)
    {
        var query = string.Join("&", new[]
        {
            $"seller={Uri.EscapeDataString(sellerId)}",
            $"order.date_last_updated.from={Uri.EscapeDataString(updatedFrom.ToString("O", CultureInfo.InvariantCulture))}",
            "sort=date_asc",
            $"limit={PageSize}",
            $"offset={offset}"
        });

        return $"https://api.mercadolibre.com/orders/search?{query}";
    }

    private static void MapOrder(SalesOrder order, JsonElement json)
    {
        order.ExternalPackId = ReadString(json, "pack_id");
        order.Status = ReadString(json, "status") ?? "unknown";
        order.StatusDetail = ReadString(json, "status_detail");
        order.CurrencyId = ReadString(json, "currency_id");
        order.TotalAmount = ReadDecimal(json, "total_amount") ?? 0m;
        order.PaidAmount = ReadDecimal(json, "paid_amount") ?? 0m;
        order.DateCreated = ReadDate(json, "date_created") ?? DateTime.UtcNow;
        order.DateClosed = ReadDate(json, "date_closed");
        order.LastUpdated = ReadDate(json, "last_updated") ??
                            order.DateClosed ??
                            order.DateCreated;
        order.SyncedAt = DateTime.UtcNow;

        if (json.TryGetProperty("buyer", out var buyer) && buyer.ValueKind == JsonValueKind.Object)
        {
            order.BuyerId = ReadString(buyer, "id");
            order.BuyerNickname = ReadString(buyer, "nickname");
        }

        if (json.TryGetProperty("shipping", out var shipping) && shipping.ValueKind == JsonValueKind.Object)
            order.ShippingId = ReadString(shipping, "id");

        if (!json.TryGetProperty("order_items", out var items) || items.ValueKind != JsonValueKind.Array)
            return;

        foreach (var itemJson in items.EnumerateArray())
        {
            var item = itemJson.TryGetProperty("item", out var itemElement)
                ? itemElement
                : default;

            order.Items.Add(new SalesOrderItem
            {
                ExternalItemId = item.ValueKind == JsonValueKind.Object
                    ? ReadString(item, "id") ?? string.Empty
                    : string.Empty,
                ExternalVariationId = item.ValueKind == JsonValueKind.Object
                    ? ReadString(item, "variation_id")
                    : null,
                Title = item.ValueKind == JsonValueKind.Object ? ReadString(item, "title") : null,
                SellerSku = item.ValueKind == JsonValueKind.Object ? ReadString(item, "seller_sku") : null,
                Quantity = ReadInt32(itemJson, "quantity") ?? 0,
                UnitPrice = ReadDecimal(itemJson, "unit_price") ?? 0m,
                SaleFee = ReadDecimal(itemJson, "sale_fee"),
                CurrencyId = ReadString(itemJson, "currency_id") ?? order.CurrencyId
            });
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText().Trim('"');
    }

    private static decimal? ReadDecimal(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDecimal(out var number))
            return number;

        return decimal.TryParse(
            property.GetString(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    private static int? ReadInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
            return number;

        return int.TryParse(property.GetString(), out var parsed) ? parsed : null;
    }

    private static DateTime? ReadDate(JsonElement element, string propertyName)
    {
        var value = ReadString(element, propertyName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed.UtcDateTime
            : null;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
