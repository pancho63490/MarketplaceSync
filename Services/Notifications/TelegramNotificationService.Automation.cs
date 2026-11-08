using System.Globalization;
using System.Text;
using MarketplaceSync.Services.Automation;
using MarketplaceSync.Services.Shipping;
using MarketplaceSync.Web.Models.Automation;
using MarketplaceSync.Web.Models.Notifications;
using MarketplaceSync.Web.Models.Orders;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Notifications;

/// <summary>
/// Lo automático por Telegram: asistente en lenguaje natural, acciones con botón de confirmar,
/// alertas de precio, etiquetas del día y la compra semanal.
/// </summary>
public sealed partial class TelegramNotificationService
{
    private static readonly CultureInfo MxCulture = CultureInfo.GetCultureInfo("es-MX");

    private T Service<T>() where T : notnull => _services.GetRequiredService<T>();

    // ─────────────────────────────────────────────────────────────
    // Asistente
    // ─────────────────────────────────────────────────────────────

    private async Task HandleAssistantAsync(string token, NotificationSettings settings, string text, long? userMessageId, CancellationToken cancellationToken)
    {
        var chatId = settings.TelegramChatId!.Value;
        var assistant = Service<TelegramAssistantService>();
        if (!assistant.IsConfigured)
        {
            await _telegram.SendMessageAsync(token, chatId,
                "Para platicar conmigo hace falta la llave de la IA (<code>Anthropic__ApiKey</code> en Render). Mientras, usa /ayuda para ver los comandos.",
                replyToMessageId: userMessageId, cancellationToken: cancellationToken);
            return;
        }

        var reply = await assistant.AskAsync(settings.OrganizationId, text, cancellationToken);
        await _telegram.SendMessageAsync(token, chatId, E(reply.Text), replyToMessageId: userMessageId, cancellationToken: cancellationToken);

        foreach (var action in reply.Actions)
            await SendActionCardAsync(token, chatId, action, cancellationToken);
        if (reply.SendLabels)
            await SendLabelsAsync(token, settings, force: true, cancellationToken);
        if (reply.SendPurchaseList)
            await SendPurchaseListAsync(token, settings, force: true, cancellationToken);
    }

    private async Task SendActionCardAsync(string token, long chatId, BotAction action, CancellationToken cancellationToken)
    {
        var buttons = new List<IReadOnlyList<TelegramButton>>
        {
            new[] { new TelegramButton("✅ Confirmar", CallbackData: $"ax:{action.Id}"), new TelegramButton("✖️ Cancelar", CallbackData: $"an:{action.Id}") }
        };
        var messageId = await _telegram.SendMessageAsync(token, chatId, "❓ " + E(action.Description) + "?", buttons, cancellationToken: cancellationToken);
        await _context.BotActions.Where(a => a.Id == action.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.TelegramMessageId, messageId), cancellationToken);
    }

    /// <summary>Botones ax (confirmar), an (cancelar/ignorar) y ap (pausar en lugar de subir).</summary>
    private async Task<bool> HandleAutomationCallbackAsync(string token, NotificationSettings settings, TelegramIncoming update, CancellationToken cancellationToken)
    {
        var data = update.CallbackData ?? string.Empty;
        var parts = data.Split(':');
        if (parts.Length != 2)
            return false;
        var org = settings.OrganizationId;
        var chatId = update.ChatId;

        switch (parts[0])
        {
            case "ax" or "an" or "ap" when long.TryParse(parts[1], out var actionId):
            {
                var actions = Service<BotActionService>();
                await _telegram.AnswerCallbackAsync(token, update.CallbackId!, parts[0] == "an" ? "Listo" : "Haciéndolo en Mercado Libre…", cancellationToken);
                string html;
                if (parts[0] == "an")
                {
                    html = "✖️ " + E(await actions.CancelAsync(org, actionId, cancellationToken));
                }
                else if (parts[0] == "ap")
                {
                    var original = await _context.BotActions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == actionId && a.OrganizationId == org, cancellationToken);
                    if (original?.PublicationId is not { } publicationId || original.Status != BotActionStatus.Pending)
                    {
                        html = "Esa propuesta ya no está vigente.";
                    }
                    else
                    {
                        await actions.CancelAsync(org, actionId, cancellationToken);
                        var pause = await actions.ProposeAsync(org, BotActionKinds.Pause, publicationId, null,
                            "Pausar " + (original.Description.Split('«', '»') is { Length: >= 2 } t ? "«" + t[1] + "»" : "la publicación"), original.Source, cancellationToken);
                        var (ok, message) = await actions.ExecuteAsync(org, pause.Id, cancellationToken);
                        html = (ok ? "⏸ " : "⚠️ ") + E(message);
                    }
                }
                else
                {
                    var (ok, message) = await actions.ExecuteAsync(org, actionId, cancellationToken);
                    html = (ok ? "✅ " : "⚠️ ") + E(message);
                }
                if (update.CallbackMessageId is { } messageId)
                    await _telegram.EditMessageAsync(token, chatId, messageId, html, null, cancellationToken);
                return true;
            }

            case "pk" when int.TryParse(parts[1], out var orderId):
            {
                var order = await _context.SalesOrders.FirstOrDefaultAsync(o => o.Id == orderId && o.OrganizationId == org, cancellationToken);
                if (order is null)
                {
                    await _telegram.AnswerCallbackAsync(token, update.CallbackId!, "Ya no encontré ese pedido.", cancellationToken);
                    return true;
                }
                order.PackedAt ??= DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);
                await _telegram.AnswerCallbackAsync(token, update.CallbackId!, "Marcado como empacado", cancellationToken);
                if (update.CallbackMessageId is { } messageId)
                {
                    var (html, buttons) = await PackingListAsync(org, cancellationToken);
                    await _telegram.EditMessageAsync(token, chatId, messageId, html, buttons, cancellationToken);
                }
                return true;
            }

            case "wl" or "ws" or "wr" when int.TryParse(parts[1], out var listId):
            {
                var purchases = Service<WeeklyPurchaseService>();
                await _telegram.AnswerCallbackAsync(token, update.CallbackId!, parts[0] == "ws" ? "Listo" : "Un momento…", cancellationToken);
                var result = parts[0] switch
                {
                    "wl" => await purchases.ApproveAsync(org, listId, cancellationToken),
                    "ws" => await purchases.SkipAsync(org, listId, cancellationToken),
                    _ => await purchases.ReceiveAsync(org, listId, cancellationToken)
                };
                var list = await purchases.LoadAsync(org, listId, cancellationToken);
                if (list is not null && update.CallbackMessageId is { } messageId)
                {
                    var (html, buttons) = await PurchaseListCardAsync(org, list, cancellationToken);
                    await _telegram.EditMessageAsync(token, chatId, messageId, html + "\n\n<i>" + E(result) + "</i>", buttons, cancellationToken);
                }
                return true;
            }
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────
    // Lo diario: precios, etiquetas y compra semanal
    // ─────────────────────────────────────────────────────────────

    /// <summary>Propuestas que hizo el piloto (compatibilidades) y todavía no se mandaron a Telegram.</summary>
    private async Task SendPendingProposalsAsync(string token, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddDays(-2);
        var pending = await _context.BotActions
            .Where(a => a.OrganizationId == settings.OrganizationId && a.Source == "autopilot" &&
                        a.Status == BotActionStatus.Pending && a.TelegramMessageId == null && a.CreatedAt >= since)
            .OrderBy(a => a.Id)
            .Take(5)
            .ToListAsync(cancellationToken);
        foreach (var action in pending)
        {
            var buttons = new List<IReadOnlyList<TelegramButton>>
            {
                new[] { new TelegramButton("✅ Enviar a Mercado Libre", CallbackData: $"ax:{action.Id}"), new TelegramButton("No", CallbackData: $"an:{action.Id}") }
            };
            if (settings.PublicBaseUrl is not null && action.PublicationId is { } pid)
                buttons.Add(new[] { new TelegramButton("Revisar en la app", Url: AppUrl(settings, $"/Publications/Edit/{pid}#pe-compat")) });
            var html = "🚗 <b>Compatibilidades sugeridas</b>\n" + E(action.Description) +
                       "\n<i>Revisa que la pieza de verdad les quede antes de enviarlas.</i>";
            action.TelegramMessageId = await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value, html, buttons, cancellationToken: cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task RunDailyAutomationsAsync(string token, NotificationSettings settings, CancellationToken cancellationToken)
    {
        await SendPendingProposalsAsync(token, settings, cancellationToken);
        var automation = await Service<AutomationService>().GetAsync(settings.OrganizationId, cancellationToken);
        var now = LocalNow;
        var today = DateOnly.FromDateTime(now);

        // Cada envío se "aparta" en la base con un UPDATE condicional: si dos revisiones corren a la vez,
        // solo una lo manda.
        if (automation.LabelsDaily && automation.LabelsSentOn != today && now.Hour >= automation.LabelsHour && now.Hour < 20)
        {
            var claimed = await _context.AutomationSettings
                .Where(a => a.Id == automation.Id && (a.LabelsSentOn == null || a.LabelsSentOn != today))
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.LabelsSentOn, today), cancellationToken);
            if (claimed == 1)
                await SendLabelsAsync(token, settings, force: false, cancellationToken);
        }

        if (automation.WeeklyPurchaseEnabled && automation.WeeklyPurchaseSentOn != today &&
            (int)now.DayOfWeek == automation.WeeklyPurchaseDay && now.Hour >= 8 && now.Hour < 20)
        {
            var claimed = await _context.AutomationSettings
                .Where(a => a.Id == automation.Id && (a.WeeklyPurchaseSentOn == null || a.WeeklyPurchaseSentOn != today))
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.WeeklyPurchaseSentOn, today), cancellationToken);
            if (claimed == 1)
                await SendPurchaseListAsync(token, settings, force: false, cancellationToken);
        }
    }

    /// <summary>Revisión de precios (después de actualizar el tipo de cambio): avisa o aplica según Automático.</summary>
    public async Task ReviewPricesAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var settings = await _context.NotificationSettings.FirstOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        var token = settings is null ? null : TokenFor(settings);
        if (settings?.TelegramChatId is not { } chatId || token is null)
            return;

        var (items, more) = await Service<RepricingService>().ReviewAsync(organizationId, cancellationToken);
        foreach (var item in items)
        {
            if (item.Applied)
            {
                await _telegram.SendMessageAsync(token, chatId,
                    $"🔁 <b>Subí el precio</b> de {E(Short(item.Title, 60))}: {Money(item.CurrentPrice)} → <b>{Money(item.SuggestedPrice)}</b>.\n" +
                    $"Con el dólar y tus costos de hoy, a {Money(item.CurrentPrice)} {(item.ProfitNow < 0 ? "perdía" : "ganaba")} {Money(Math.Abs(item.ProfitNow))}; ahora gana {Money(item.ProfitAtSuggested)} por pieza.",
                    cancellationToken: cancellationToken);
                continue;
            }

            if (item.Error is not null)
            {
                await _telegram.SendMessageAsync(token, chatId,
                    $"⚠️ No pude subir el precio de {E(Short(item.Title, 60))}: {E(item.Error)}", cancellationToken: cancellationToken);
                continue;
            }

            var html = $"💸 <b>{(item.ProfitNow < 0 ? "Esta publicación pierde" : "Ya no gana lo mínimo")}</b>\n{E(Short(item.Title, 80))}\n" +
                       $"A {Money(item.CurrentPrice)} {(item.ProfitNow < 0 ? "pierdes" : "ganas")} {Money(Math.Abs(item.ProfitNow))} por pieza (dólar y costos de hoy).\n" +
                       $"A <b>{Money(item.SuggestedPrice)}</b> ganarías {Money(item.ProfitAtSuggested)}.";
            var buttons = new List<IReadOnlyList<TelegramButton>>
            {
                new[] { new TelegramButton($"✅ Subir a {Money(item.SuggestedPrice)}", CallbackData: $"ax:{item.ActionId}") },
                new[] { new TelegramButton("⏸ Pausar", CallbackData: $"ap:{item.ActionId}"), new TelegramButton("Ignorar", CallbackData: $"an:{item.ActionId}") }
            };
            var messageId = await _telegram.SendMessageAsync(token, chatId, html, buttons, cancellationToken: cancellationToken);
            await _context.BotActions.Where(a => a.Id == item.ActionId)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.TelegramMessageId, messageId), cancellationToken);
        }

        if (more > 0)
            await _telegram.SendMessageAsync(token, chatId,
                $"Y {more} {(more == 1 ? "publicación más" : "publicaciones más")} sin llegar a la ganancia mínima: velas en Publicados → Con pérdida.",
                settings.PublicBaseUrl is null ? null : new[] { new[] { new TelegramButton("Abrir Publicados", Url: AppUrl(settings, "/Publications?filter=loss")) } },
                cancellationToken: cancellationToken);
    }

    // ── Etiquetas ──

    private async Task SendLabelsAsync(string token, NotificationSettings settings, bool force, CancellationToken cancellationToken)
    {
        var org = settings.OrganizationId;
        var chatId = settings.TelegramChatId!.Value;
        var orderIds = await _context.SalesOrders.AsNoTracking()
            .Where(o => o.OrganizationId == org && o.PackedAt == null)
            .ToShip()
            .OrderBy(o => o.ShippingDeadline)
            .Select(o => o.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        if (orderIds.Count == 0)
        {
            if (force)
                await _telegram.SendMessageAsync(token, chatId, "📦 No hay pedidos por enviar. 🎉", cancellationToken: cancellationToken);
            return;
        }

        var automation = await Service<AutomationService>().GetAsync(org, cancellationToken);
        try
        {
            var (file, error) = await Service<ShippingLabelService>().GetLabelsAsync(org, orderIds, automation.LabelsThermal, cancellationToken);
            if (file is null)
            {
                await _telegram.SendMessageAsync(token, chatId, "⚠️ No pude bajar las etiquetas: " + E(error), cancellationToken: cancellationToken);
            }
            else
            {
                var caption = $"🏷 <b>{file.Labels} {(file.Labels == 1 ? "etiqueta" : "etiquetas")}</b> para hoy." +
                              (file.Skipped.Count > 0 ? $"\nNo incluidas: {E(string.Join("; ", file.Skipped.Take(4)))}" : "");
                await _telegram.SendDocumentAsync(token, chatId, file.Content, file.FileName, file.ContentType, caption, cancellationToken: cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not TelegramApiException { IsUnauthorizedOrBlocked: true })
        {
            _logger.LogWarning(exception, "Daily labels failed for {Org}.", org);
            await _telegram.SendMessageAsync(token, chatId, "⚠️ Mercado Libre no entregó las etiquetas ahorita. Escribe /etiquetas en un rato.", cancellationToken: cancellationToken);
        }

        var (html, buttons) = await PackingListAsync(org, cancellationToken);
        await _telegram.SendMessageAsync(token, chatId, html, buttons, cancellationToken: cancellationToken);
    }

    /// <summary>Qué empacar (piezas agrupadas) y un botón "Empacado" por pedido pendiente.</summary>
    private async Task<(string Html, IReadOnlyList<IReadOnlyList<TelegramButton>>? Buttons)> PackingListAsync(Guid org, CancellationToken cancellationToken)
    {
        var orders = await _context.SalesOrders.AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.OrganizationId == org)
            .ToShip()
            .OrderBy(o => o.ShippingDeadline)
            .Take(30)
            .ToListAsync(cancellationToken);

        var pending = orders.Where(o => o.PackedAt == null).ToList();
        if (orders.Count == 0)
            return ("📦 No hay pedidos por enviar.", null);

        var sb = new StringBuilder();
        sb.AppendLine(pending.Count == 0
            ? "✅ <b>Todo empacado</b>. Solo falta entregarlo."
            : $"📦 <b>Por empacar: {pending.Count} {(pending.Count == 1 ? "pedido" : "pedidos")}</b>");
        foreach (var group in pending.SelectMany(o => o.Items).GroupBy(i => i.Title ?? i.ExternalItemId).OrderByDescending(g => g.Sum(i => i.Quantity)))
            sb.AppendLine($"• {group.Sum(i => i.Quantity)} × {E(Short(group.Key, 60))}");
        var late = pending.Count(o => o.ShippingDeadline < DateTime.UtcNow);
        if (late > 0)
            sb.AppendLine($"\n⚠️ {late} {(late == 1 ? "va" : "van")} tarde: empácalos primero.");

        var buttons = pending.Take(12)
            .Select(o => (IReadOnlyList<TelegramButton>)new[]
            {
                new TelegramButton($"📦 Empacado: {Short(o.Items.FirstOrDefault()?.Title ?? o.ExternalOrderId, 28)}" +
                                   (o.Items.Sum(i => i.Quantity) > 1 ? $" ×{o.Items.Sum(i => i.Quantity)}" : ""),
                    CallbackData: $"pk:{o.Id}")
            })
            .ToList();
        return (sb.ToString().TrimEnd(), buttons.Count == 0 ? null : buttons);
    }

    // ── Compra semanal ──

    private async Task SendPurchaseListAsync(string token, NotificationSettings settings, bool force, CancellationToken cancellationToken)
    {
        var org = settings.OrganizationId;
        var chatId = settings.TelegramChatId!.Value;
        var automation = await Service<AutomationService>().GetAsync(org, cancellationToken);
        var list = await Service<WeeklyPurchaseService>().BuildAsync(org, automation.WeeklyPurchaseIncludeNew, cancellationToken);
        if (list is null)
        {
            if (force)
                await _telegram.SendMessageAsync(token, chatId, "🛒 Esta semana no hay nada que recomprar: tu stock alcanza para lo que se vende.", cancellationToken: cancellationToken);
            return;
        }

        var (html, buttons) = await PurchaseListCardAsync(org, list, cancellationToken);
        var messageId = await _telegram.SendMessageAsync(token, chatId, html, buttons, cancellationToken: cancellationToken);
        list.TelegramMessageId = messageId;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<(string Html, IReadOnlyList<IReadOnlyList<TelegramButton>>? Buttons)> PurchaseListCardAsync(Guid org, PurchaseList list, CancellationToken cancellationToken)
    {
        var rate = await _context.CrossBorderSettings.AsNoTracking()
            .Where(s => s.OrganizationId == org).Select(s => (decimal?)s.UsdToMxnRate).FirstOrDefaultAsync(cancellationToken) ?? 20m;

        var sb = new StringBuilder();
        sb.AppendLine(list.Status switch
        {
            "approved" => "🛒 <b>Compra aprobada</b> — cuando llegue a Juárez pulsa «Ya llegó».",
            "received" => "📦 <b>Compra recibida</b> — stock sumado.",
            "skipped" => "🛒 <b>Compra omitida</b> esta semana.",
            _ => "🛒 <b>Compra de la semana</b>"
        });
        foreach (var item in list.Items)
        {
            var cost = item.UnitCostUsd is > 0 ? $" · US${item.UnitCostUsd.Value.ToString("0.00", CultureInfo.InvariantCulture)} c/u" : " · sin costo";
            var name = item.Url is not null ? $"<a href=\"{E(item.Url)}\">{E(Short(item.Title, 55))}</a>" : E(Short(item.Title, 55));
            sb.AppendLine($"• <b>{item.Quantity}</b> × {name}{cost}" + (item.Reason is null ? "" : $"\n   <i>{E(item.Reason)}</i>"));
        }
        sb.AppendLine($"\nTotal: <b>US${list.TotalUsd.ToString("N2", CultureInfo.InvariantCulture)}</b> (≈ {Money(list.TotalUsd * rate)} a {rate.ToString("0.00", CultureInfo.InvariantCulture)})");

        IReadOnlyList<IReadOnlyList<TelegramButton>>? buttons = list.Status switch
        {
            "proposed" => new[] { new[] { new TelegramButton("✅ Aprobar", CallbackData: $"wl:{list.Id}"), new TelegramButton("Omitir", CallbackData: $"ws:{list.Id}") } },
            "approved" => new[] { new[] { new TelegramButton("📦 Ya llegó todo", CallbackData: $"wr:{list.Id}") } },
            _ => null
        };
        return (sb.ToString().TrimEnd(), buttons);
    }

    private static string Money(decimal value) => value.ToString("C0", MxCulture);

    private static string Short(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(sin título)";
        value = value.Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }
}
