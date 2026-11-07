using System.Globalization;
using System.Text.Json;
using MarketplaceSync.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.CrossBorder;

public sealed record OfficialRate(decimal Rate, string Source, DateOnly? Date);

/// <summary>
/// Tipo de cambio peso-dólar del día. Con <c>Banxico:Token</c> usa el FIX de Banxico (serie SF43718);
/// sin token, el de referencia del Banco Central Europeo (Frankfurter). Se le suma el margen que el vendedor
/// configura (lo que cobra su tarjeta o casa de cambio).
/// </summary>
public sealed class ExchangeRateService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly AppDbContext _context;
    private readonly ILogger<ExchangeRateService> _logger;

    public ExchangeRateService(IHttpClientFactory httpClientFactory, IConfiguration configuration, AppDbContext context, ILogger<ExchangeRateService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _context = context;
        _logger = logger;
    }

    /// <summary>Tipo de cambio oficial de hoy, o null si ninguna fuente respondió.</summary>
    public async Task<OfficialRate?> GetOfficialAsync(CancellationToken cancellationToken = default)
    {
        var token = _configuration["Banxico:Token"];
        if (!string.IsNullOrWhiteSpace(token))
        {
            var banxico = await TryBanxicoAsync(token, cancellationToken);
            if (banxico is not null)
                return banxico;
        }
        return await TryFrankfurterAsync(cancellationToken);
    }

    /// <summary>Actualiza las organizaciones con tipo de cambio automático. Regresa cuántas cambió.</summary>
    public async Task<int> UpdateAllAsync(Guid? onlyOrganization = null, CancellationToken cancellationToken = default)
    {
        var official = await GetOfficialAsync(cancellationToken);
        if (official is null)
            return 0;

        var settings = await _context.CrossBorderSettings
            .Where(s => s.AutoExchangeRate && (onlyOrganization == null || s.OrganizationId == onlyOrganization))
            .ToListAsync(cancellationToken);
        foreach (var item in settings)
        {
            item.OfficialUsdToMxnRate = official.Rate;
            item.UsdToMxnRate = Math.Round(official.Rate * (1 + Math.Max(0m, item.ExchangeRateMarkupPercent) / 100m), 4);
            item.ExchangeRateUpdatedAt = DateTime.UtcNow;
            item.ExchangeRateSource = official.Date is { } date
                ? $"{official.Source} del {date.ToString("d MMM", CultureInfo.GetCultureInfo("es-MX"))}"
                : official.Source;
            item.UpdatedAt = DateTime.UtcNow;
        }
        await _context.SaveChangesAsync(cancellationToken);
        return settings.Count;
    }

    private async Task<OfficialRate?> TryBanxicoAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{Base("Banxico:BaseUrl", "https://www.banxico.org.mx")}/SieAPIRest/service/v1/series/SF43718/datos/oportuno");
            request.Headers.Add("Bmx-Token", token);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Banxico returned {Status}.", (int)response.StatusCode);
                return null;
            }

            using var document = JsonDocument.Parse(content);
            var dato = document.RootElement.GetProperty("bmx").GetProperty("series")[0].GetProperty("datos")[0];
            var rate = decimal.Parse(dato.GetProperty("dato").GetString()!.Replace(",", ""), CultureInfo.InvariantCulture);
            DateOnly? date = DateOnly.TryParseExact(dato.GetProperty("fecha").GetString(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            return Sane(rate) ? new OfficialRate(Math.Round(rate, 4), "FIX Banxico", date) : null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Could not read the Banxico FIX rate.");
            return null;
        }
    }

    private async Task<OfficialRate?> TryFrankfurterAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using var response = await client.GetAsync(
                $"{Base("Frankfurter:BaseUrl", "https://api.frankfurter.app")}/latest?from=USD&to=MXN", cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Frankfurter returned {Status}.", (int)response.StatusCode);
                return null;
            }

            using var document = JsonDocument.Parse(content);
            var rate = document.RootElement.GetProperty("rates").GetProperty("MXN").GetDecimal();
            DateOnly? date = document.RootElement.TryGetProperty("date", out var dateElement) &&
                             DateOnly.TryParse(dateElement.GetString(), CultureInfo.InvariantCulture, out var d) ? d : null;
            return Sane(rate) ? new OfficialRate(Math.Round(rate, 4), "Banco Central Europeo", date) : null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Could not read the exchange rate.");
            return null;
        }
    }

    // Un valor fuera de rango es un error de la fuente: mejor no tocar los precios.
    private static bool Sane(decimal rate) => rate is > 10m and < 40m;

    private string Base(string key, string fallback) =>
        _configuration[key] is { Length: > 0 } configured ? configured.TrimEnd('/') : fallback;
}
