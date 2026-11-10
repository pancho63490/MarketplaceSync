using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Automation;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Automation;

/// <summary>Una publicación que dejó de ganar lo mínimo y qué se hizo con ella.</summary>
public sealed record RepriceItem(
    int PublicationId, string ItemId, string Title, decimal CurrentPrice, decimal SuggestedPrice,
    decimal ProfitNow, decimal ProfitAtSuggested, bool Applied, long? ActionId, string? Error);

/// <summary>
/// Revisa las publicaciones activas con costo conocido (cuando cambian el dólar o el costo) y, si alguna ya no
/// gana lo mínimo, propone el precio sugerido (Telegram con botones) o lo aplica solo en modo automático hasta
/// el tope de subida. Nunca baja precios. No repite una propuesta igual en 3 días ni una ignorada en 14.
/// </summary>
public sealed class RepricingService
{
    public const int MaxPerRun = 10;

    private readonly AppDbContext _context;
    private readonly AutomationService _automation;
    private readonly ICrossBorderPricingService _pricing;
    private readonly PublicationEconomics _economics;
    private readonly BotActionService _actions;
    private readonly MercadoLibreItemEditorService _editor;
    private readonly ILogger<RepricingService> _logger;

    public RepricingService(
        AppDbContext context,
        AutomationService automation,
        ICrossBorderPricingService pricing,
        PublicationEconomics economics,
        BotActionService actions,
        MercadoLibreItemEditorService editor,
        ILogger<RepricingService> logger)
    {
        _context = context;
        _automation = automation;
        _pricing = pricing;
        _economics = economics;
        _actions = actions;
        _editor = editor;
        _logger = logger;
    }

    public async Task<(IReadOnlyList<RepriceItem> Items, int MoreNotShown)> ReviewAsync(Guid org, CancellationToken cancellationToken = default)
    {
        var automation = await _automation.GetAsync(org, cancellationToken);
        if (automation.RepricingMode == RepricingModes.Off)
            return (Array.Empty<RepriceItem>(), 0);

        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var publications = await _context.MarketplacePublications.AsNoTracking()
            .Include(p => p.Product)
            .Where(p => p.Product.OrganizationId == org && p.Marketplace == "MercadoLibre" && p.IsPublished &&
                        p.ExternalItemId != null && p.Status == "active" && p.Price > 0 &&
                        p.PromotionType == null && // subir el precio le quitaría el descuento
                        p.AutoPriceStatus == null) // con precio automático, la app solo cuida el mínimo
            .ToListAsync(cancellationToken);

        var candidates = publications
            .Select(p => (Publication: p, Money: _economics.Calculate(p, p.Product, settings)))
            .Where(x => x.Money is { NeedsRaise: true })
            .OrderBy(x => x.Money!.Profit.NetProfitMxn) // primero las que más pierden
            .ToList();
        if (candidates.Count == 0)
            return (Array.Empty<RepriceItem>(), 0);

        var ids = candidates.Select(c => c.Publication.Id).ToList();
        var recent = await _context.BotActions.AsNoTracking()
            .Where(a => a.OrganizationId == org && a.Source == "repricing" && a.PublicationId != null &&
                        ids.Contains(a.PublicationId.Value) && a.CreatedAt >= DateTime.UtcNow.AddDays(-14))
            .ToListAsync(cancellationToken);

        var items = new List<RepriceItem>();
        var skippedForLimit = 0;
        foreach (var (publication, money) in candidates)
        {
            var target = money!.SuggestedPriceMxn;
            var history = recent.Where(a => a.PublicationId == publication.Id).ToList();
            if (history.Any(a => a.Status == BotActionStatus.Cancelled))
                continue; // el vendedor dijo "Ignorar": no insistir en 14 días
            if (history.Any(a => a.CreatedAt >= DateTime.UtcNow.AddDays(-3) && a.Value == target &&
                                 a.Status is BotActionStatus.Pending or BotActionStatus.Done or BotActionStatus.Failed))
                continue;

            if (items.Count >= MaxPerRun)
            {
                skippedForLimit++;
                continue;
            }

            var current = publication.Price!.Value;
            var title = publication.Product.Title ?? publication.ExternalItemId!;
            var atTarget = _economics.Calculate(publication, publication.Product, settings, target)!;
            var increase = (target - current) / current * 100m;
            var description = $"Subir «{Short(title, 50)}» de {BotActionService.Money(current)} a {BotActionService.Money(target)} " +
                              $"(hoy {(money.Profit.NetProfitMxn < 0 ? "pierde" : "gana")} {BotActionService.Money(Math.Abs(money.Profit.NetProfitMxn))}; " +
                              $"a ese precio gana {BotActionService.Money(atTarget.Profit.NetProfitMxn)})";

            var action = await _actions.ProposeAsync(org, BotActionKinds.Price, publication.Id, target, description, "repricing", cancellationToken);
            if (automation.RepricingMode == RepricingModes.Auto && increase <= automation.RepricingMaxIncreasePercent)
            {
                var (ok, message) = await _actions.ExecuteAsync(org, action.Id, cancellationToken);
                items.Add(new RepriceItem(publication.Id, publication.ExternalItemId!, title, current, target,
                    money.Profit.NetProfitMxn, atTarget.Profit.NetProfitMxn, ok, action.Id, ok ? null : message));
            }
            else
            {
                items.Add(new RepriceItem(publication.Id, publication.ExternalItemId!, title, current, target,
                    money.Profit.NetProfitMxn, atTarget.Profit.NetProfitMxn, false, action.Id, null));
            }
        }

        if (items.Count > 0)
            _logger.LogInformation("Repricing for {Org}: {Count} publication(s), {Applied} applied.", org, items.Count, items.Count(i => i.Applied));
        return (items, skippedForLimit);
    }

    private static string Short(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
