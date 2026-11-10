using System.Globalization;
using System.Text;
using MarketplaceSync.Services.Automation;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Models.Automation;
using MarketplaceSync.Web.Models.Notifications;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Notifications;

/// <summary>Catálogo (perder o recuperar el botón de compra) y calidad de publicaciones por Telegram.</summary>
public sealed partial class TelegramNotificationService
{
    /// <summary>Cada 2 horas: revisa el catálogo y avisa cuando una publicación empieza a perder o vuelve a ganar.</summary>
    public async Task RefreshCatalogAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var org in await ConnectedOrganizationsAsync(cancellationToken))
        {
            try
            {
                var changes = await Service<CatalogCompetitionService>().RefreshAsync(org, 80, force: false, cancellationToken);
                if (changes.Count > 0)
                    await NotifyCatalogChangesAsync(org, changes, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Catalog check failed for {Org}.", org);
            }
            finally
            {
                _context.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Una vez al día: califica (con Mercado Libre) las publicaciones activas que tienen más de 3 días sin revisar.</summary>
    public async Task RefreshListingQualityAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var org in await ConnectedOrganizationsAsync(cancellationToken))
        {
            try
            {
                await Service<ListingQualityService>().RefreshAsync(org, 120, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Listing quality check failed for {Org}.", org);
            }
            finally
            {
                _context.ChangeTracker.Clear();
            }
        }
    }

    private Task<List<Guid>> ConnectedOrganizationsAsync(CancellationToken cancellationToken) =>
        _context.MarketplaceAccounts.AsNoTracking()
            .Where(a => a.Marketplace == "MercadoLibre" && a.Status == "Connected")
            .Select(a => a.OrganizationId).Distinct().ToListAsync(cancellationToken);

    private async Task NotifyCatalogChangesAsync(Guid org, List<CatalogChange> changes, CancellationToken cancellationToken)
    {
        var settings = await _context.NotificationSettings.FirstOrDefaultAsync(x => x.OrganizationId == org, cancellationToken);
        var token = settings is null ? null : TokenFor(settings);
        if (settings?.TelegramChatId is not { } chatId || token is null)
            return;
        var pricing = await Service<ICrossBorderPricingService>().GetOrCreateSettingsAsync(org, cancellationToken);
        var economics = Service<PublicationEconomics>();
        var day = LocalNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var interesting = changes
            .Where(c => c.NewStatus == CatalogCompetitionService.Losing ||
                        (c.OldStatus == CatalogCompetitionService.Losing && c.NewStatus is CatalogCompetitionService.Winning or CatalogCompetitionService.Sharing))
            .OrderByDescending(c => c.Publication.SoldQuantity ?? 0)
            .Take(6);
        foreach (var change in interesting)
        {
            var p = change.Publication;
            var key = $"cat:{p.Id}:{change.NewStatus}:{day}";
            if (await _context.NotificationLogs.AnyAsync(l => l.OrganizationId == org && l.Key == key, cancellationToken)) continue;
            var title = Short(p.Product.Title, 70);

            if (change.NewStatus != CatalogCompetitionService.Losing)
            {
                var won = await _telegram.SendMessageAsync(token, chatId,
                    $"🏆 <b>Recuperaste el botón de compra</b> en catálogo\n{E(title)} · {Money(p.Price ?? 0)}",
                    cancellationToken: cancellationToken);
                await LogAsync(org, key, "catalog", p.Id.ToString(CultureInfo.InvariantCulture), chatId, won, cancellationToken);
                continue;
            }

            var html = new StringBuilder("🏷️ <b>Estás perdiendo en catálogo</b>\n").Append(E(title)).Append('\n')
                .Append("Tu precio: ").Append(Money(p.Price ?? 0));
            if (p.CatalogWinnerPrice is > 0) html.Append(" · ganador: ").Append(Money(p.CatalogWinnerPrice.Value));
            var reasons = CatalogCompetitionService.Split(p.CatalogReasons).Select(CatalogCompetitionService.ReasonLabel).ToList();
            if (reasons.Count > 0) html.Append("\nNo compite por precio: ").Append(E(string.Join("; ", reasons))).Append('.');
            var boosts = CatalogCompetitionService.Split(p.CatalogOpportunities).Select(CatalogCompetitionService.BoostLabel).ToList();
            if (boosts.Count > 0) html.Append("\nTe ayudaría: ").Append(E(string.Join(", ", boosts))).Append('.');

            BotAction? action = null;
            if (p.CatalogPriceToWin is > 0 && p.Price is > 0 && p.CatalogPriceToWin < p.Price)
            {
                var target = Math.Floor(p.CatalogPriceToWin.Value);
                var atTarget = economics.Calculate(p, p.Product, pricing, target);
                html.Append("\nGanarías con <b>").Append(Money(target)).Append("</b>");
                if (atTarget is null)
                {
                    html.Append(" (falta el costo para saber si te conviene).");
                }
                else if (atTarget.Profit.NetProfitMxn <= 0)
                {
                    html.Append(", pero perderías ").Append(Money(-atTarget.Profit.NetProfitMxn)).Append(" por pieza: no te conviene bajar.");
                }
                else
                {
                    html.Append(" y te quedan ").Append(Money(atTarget.Profit.NetProfitMxn)).Append(" por pieza")
                        .Append(atTarget.Profit.IsProfitable ? "." : " (menos de tu ganancia mínima).");
                    action = new BotAction
                    {
                        OrganizationId = org,
                        Kind = BotActionKinds.Price,
                        PublicationId = p.Id,
                        Value = target,
                        Description = $"Bajar «{Short(p.Product.Title, 60)}» de {Money(p.Price.Value)} a {Money(target)} para ganar el catálogo",
                        Source = "catalog"
                    };
                    // Una sola propuesta pendiente por publicación.
                    await _context.BotActions
                        .Where(a => a.OrganizationId == org && a.PublicationId == p.Id && a.Status == BotActionStatus.Pending)
                        .ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, BotActionStatus.Expired).SetProperty(a => a.DecidedAt, DateTime.UtcNow), cancellationToken);
                    _context.BotActions.Add(action);
                    await _context.SaveChangesAsync(cancellationToken);
                }
            }

            var buttons = new List<IReadOnlyList<TelegramButton>>();
            if (action is not null)
                buttons.Add(new[] { new TelegramButton($"⬇️ Bajar a {Money(action.Value!.Value)}", CallbackData: $"ax:{action.Id}"), new TelegramButton("Ignorar", CallbackData: $"an:{action.Id}") });
            if (settings.PublicBaseUrl is not null)
                buttons.Add(new[] { new TelegramButton("Ver catálogo", Url: AppUrl(settings, "/Quality#catalogo")) });
            var messageId = await _telegram.SendMessageAsync(token, chatId, html.ToString(), buttons.Count == 0 ? null : buttons, cancellationToken: cancellationToken);
            if (action is not null)
            {
                action.TelegramMessageId = messageId;
                await _context.SaveChangesAsync(cancellationToken);
            }
            await LogAsync(org, key, "catalog", p.Id.ToString(CultureInfo.InvariantCulture), chatId, messageId, cancellationToken);
        }
    }

    /// <summary>/catalogo y /calidad: cómo vas en catálogo y las publicaciones con peor calidad.</summary>
    private async Task SendQualityStatusAsync(string token, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var org = settings.OrganizationId;
        await Service<CatalogCompetitionService>().RefreshAsync(org, 40, force: false, cancellationToken);
        var rows = await Service<CatalogCompetitionService>().RowsAsync(org, cancellationToken);
        var html = new StringBuilder("📈 <b>Calidad y catálogo</b>\n");
        if (rows.Count == 0)
        {
            html.Append("No tienes publicaciones en catálogo.\n");
        }
        else
        {
            var losing = rows.Where(r => r.Publication.CatalogStatus == CatalogCompetitionService.Losing).ToList();
            var winning = rows.Count(r => r.Publication.CatalogStatus is CatalogCompetitionService.Winning or CatalogCompetitionService.Sharing);
            html.Append("Catálogo: ").Append(winning).Append(" ganando · ").Append(losing.Count).Append(" perdiendo\n");
            foreach (var r in losing.Take(5))
            {
                html.Append("• ").Append(E(Short(r.Publication.Product.Title, 45))).Append(": ").Append(Money(r.Publication.Price ?? 0));
                if (r.Publication.CatalogPriceToWin is > 0 && r.Publication.CatalogPriceToWin >= r.Publication.Price)
                    html.Append(" (ya tienes el precio; Mercado Libre aún no lo recalcula)");
                else if (r.Publication.CatalogPriceToWin is > 0)
                    html.Append(" → gana con ").Append(Money(Math.Floor(r.Publication.CatalogPriceToWin.Value)))
                        .Append(r.AtPriceToWin is { } win ? win.Profit.NetProfitMxn > 0 ? $" (te quedan {Money(win.Profit.NetProfitMxn)})" : " (perderías)" : "");
                html.Append('\n');
            }
        }

        var active = _context.MarketplacePublications.AsNoTracking()
            .Where(p => p.Product.OrganizationId == org && p.IsPublished && p.Status == "active" && p.QualityScore != null);
        var count = await active.CountAsync(cancellationToken);
        if (count > 0)
        {
            var average = await active.AverageAsync(p => (double)p.QualityScore!.Value, cancellationToken);
            var basic = await active.CountAsync(p => p.QualityLevel == "Basic", cancellationToken);
            html.Append("\nCalidad promedio: <b>").Append(average.ToString("0", MxCulture)).Append("/100</b>");
            if (basic > 0) html.Append(" · ").Append(basic).Append(basic == 1 ? " básica" : " básicas");
            html.Append('\n');
            var worst = await active.Include(p => p.Product).OrderBy(p => p.QualityScore).Take(3).ToListAsync(cancellationToken);
            foreach (var p in worst)
            {
                var first = ListingQualityService.Actions(p).FirstOrDefault();
                html.Append("• ").Append(p.QualityScore).Append(" · ").Append(E(Short(p.Product.Title, 45)));
                if (first is not null) html.Append(": ").Append(E(Short(first.Title, 70)));
                html.Append('\n');
            }
        }
        else
        {
            html.Append("\nLa calidad se revisa sola cada noche.");
        }

        await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value, html.ToString().Trim(),
            settings.PublicBaseUrl is null ? null : new[] { new[] { new TelegramButton("Abrir Calidad y catálogo", Url: AppUrl(settings, "/Quality")) } },
            cancellationToken: cancellationToken);
    }
}
