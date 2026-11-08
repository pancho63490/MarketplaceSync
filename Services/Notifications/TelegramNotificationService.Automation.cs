using Hangfire;
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
    // Gastos
    // ─────────────────────────────────────────────────────────────

    /// <summary>"/gasto 250 gasolina", "/gasto 35 usd render mensual", "/gasto 180.50 cajas".</summary>
    private async Task HandleExpenseCommandAsync(string token, NotificationSettings settings, string args, long? messageId, CancellationToken cancellationToken)
    {
        var parsed = ParseExpense(args);
        if (parsed is null)
        {
            await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value,
                "Dime monto y qué fue, por ejemplo:\n<code>/gasto 250 gasolina</code>\n<code>/gasto 180 cajas medianas</code>\n<code>/gasto 7 usd render mensual</code>",
                replyToMessageId: messageId, cancellationToken: cancellationToken);
            return;
        }

        var (amount, currency, description, monthly) = parsed.Value;
        var finance = Service<MarketplaceSync.Services.Finance.FinanceReportService>();
        var expense = await finance.AddExpenseAsync(settings.OrganizationId, MarketplaceSync.Services.Finance.FinanceReportService.TodayJuarez,
            MarketplaceSync.Web.Models.Finance.ExpenseCategories.Guess(description), description, amount, currency, monthly, "telegram", cancellationToken);
        var today = MarketplaceSync.Services.Finance.FinanceReportService.TodayJuarez;
        var month = await finance.MonthAsync(settings.OrganizationId, today.Year, today.Month, cancellationToken);
        await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value,
            $"🧾 Anotado: <b>{E(expense.Description)}</b> · {Money(expense.AmountMxn)}" +
            (expense.Currency == "USD" ? $" (US${expense.Amount.ToString("0.00", CultureInfo.InvariantCulture)})" : "") +
            $" · {E(MarketplaceSync.Web.Models.Finance.ExpenseCategories.Label(expense.Category))}" + (monthly ? " · cada mes" : "") +
            $"\nGastos del mes: {Money(month.ExpensesTotal)} · Ganancia neta: <b>{Money(month.NetProfit)}</b>",
            replyToMessageId: messageId, cancellationToken: cancellationToken);
    }

    public static (decimal Amount, string Currency, string Description, bool Monthly)? ParseExpense(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\$?\s*(\d+(?:[.,]\d{1,2})?)");
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            return null;
        var rest = (text[..match.Index] + " " + text[(match.Index + match.Length)..]).Trim();
        var lower = rest.ToLowerInvariant();
        var currency = System.Text.RegularExpressions.Regex.IsMatch(lower, @"\b(usd|us\$|dlls?|d[oó]lares?)\b") ? "USD" : "MXN";
        var monthly = System.Text.RegularExpressions.Regex.IsMatch(lower, @"\b(mensual|cada mes|al mes)\b");
        var description = System.Text.RegularExpressions.Regex.Replace(rest, @"(?i)\b(usd|us\$|dlls?|d[oó]lares?|pesos|mxn|mensual|cada mes|al mes|en|de)\b", " ");
        description = System.Text.RegularExpressions.Regex.Replace(description, @"\s+", " ").Trim(' ', '-', ':', ',');
        return (amount, currency, description.Length == 0 ? "Gasto" : char.ToUpper(description[0]) + description[1..], monthly);
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

        await RunInvoicingAlertsAsync(token, settings, cancellationToken);

        if (automation.PromotionAlerts && automation.PromotionsCheckedOn != today && now.Hour >= 9 && now.Hour < 20)
        {
            var claimed = await _context.AutomationSettings
                .Where(a => a.Id == automation.Id && (a.PromotionsCheckedOn == null || a.PromotionsCheckedOn != today))
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.PromotionsCheckedOn, today), cancellationToken);
            if (claimed == 1)
            {
                var org = settings.OrganizationId;
                // Lo que terminó o empezó se revisa aparte (muchas llamadas); las invitaciones van ya.
                Service<IBackgroundJobClient>().Enqueue<MarketplaceSync.Services.MercadoLibre.PromotionService>(
                    s => s.RefreshAllAsync(org, 150, CancellationToken.None));
                await SendPromotionInvitesAsync(token, settings, force: false, cancellationToken);
            }
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

    // ─────────────────────────────────────────────────────────────
    // Promociones
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Campañas de Mercado Libre con publicaciones invitadas: cuáles siguen ganando con ese precio y un botón
    /// para meter solo esas. Sin <paramref name="force"/>, cada campaña se avisa una sola vez.
    /// </summary>
    private async Task SendPromotionInvitesAsync(string token, NotificationSettings settings, bool force, CancellationToken cancellationToken)
    {
        var org = settings.OrganizationId;
        var chatId = settings.TelegramChatId!.Value;
        var promotions = Service<MarketplaceSync.Services.MercadoLibre.PromotionService>();
        List<MarketplaceSync.Services.MercadoLibre.CampaignView> campaigns;
        try
        {
            campaigns = await promotions.CampaignsAsync(org, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Reading promotions for {Org} failed.", org);
            if (force)
                await _telegram.SendMessageAsync(token, chatId, "No pude leer las promociones de Mercado Libre. Intenta en un momento.", cancellationToken: cancellationToken);
            return;
        }

        var sent = 0;
        foreach (var campaign in campaigns.Where(c => c.Candidates > 0))
        {
            var key = "promo:" + campaign.Promotion.Id;
            if (!force && (campaign.CandidatesThatEarn == 0 ||
                           await _context.NotificationLogs.AnyAsync(l => l.OrganizationId == org && l.Key == key, cancellationToken)))
                continue;

            var p = campaign.Promotion;
            var earning = campaign.Rows.Where(r => r.Offer.IsCandidate && r.Earns).ToList();
            var losing = campaign.Rows.Where(r => r.Offer.IsCandidate && r.CostKnown && !r.Earns).ToList();
            var noCost = campaign.Rows.Count(r => r.Offer.IsCandidate && !r.CostKnown);

            var html = new StringBuilder();
            html.Append("🏷️ <b>Mercado Libre te invita a «").Append(E(p.Name)).Append("»</b>\n")
                .Append(E(p.TypeLabel));
            if (p.Deadline is { } deadline && deadline > DateTime.UtcNow)
                html.Append(" · entras hasta el ").Append(deadline.AddHours(-7).ToString("d MMM", MxCulture));
            else if (p.Finish is { } finish)
                html.Append(" · termina el ").Append(finish.AddHours(-7).ToString("d MMM", MxCulture));
            html.Append('\n');

            if (earning.Count > 0)
            {
                html.Append("\n✅ <b>Siguen ganando</b> (").Append(earning.Count).Append("):\n");
                foreach (var r in earning.Take(8))
                    html.Append("• ").Append(E(Short(r.Title, 45))).Append(": ").Append(Money(r.CurrentPrice)).Append(" → <b>")
                        .Append(r.ProposedPrice is { } pp ? Money(pp) : "—").Append("</b> · ganas ").Append(Money(r.Profit ?? 0)).Append('\n');
                if (earning.Count > 8) html.Append("…y ").Append(earning.Count - 8).Append(" más\n");
            }
            if (losing.Count > 0)
            {
                html.Append("\n✖️ <b>No conviene</b> (").Append(losing.Count).Append("): ");
                html.Append(string.Join(", ", losing.Take(4).Select(r =>
                    E(Short(r.Title, 30)) + (r.Profit < 0 ? " (pierdes " + Money(Math.Abs(r.Profit ?? 0)) + ")" : " (ganas " + Money(r.Profit ?? 0) + ")"))));
                if (losing.Count > 4) html.Append(" y ").Append(losing.Count - 4).Append(" más");
                html.Append('\n');
            }
            if (noCost > 0)
                html.Append("\n").Append(noCost).Append(noCost == 1 ? " sin costo capturado (no sé si gana)." : " sin costo capturado (no sé si ganan).").Append('\n');

            var buttons = new List<IReadOnlyList<TelegramButton>>();
            long? actionId = null;
            if (earning.Count > 0)
            {
                var payload = new MarketplaceSync.Services.MercadoLibre.PromotionJoinPayload
                {
                    PromotionId = p.Id,
                    Type = p.Type,
                    SubType = p.SubType,
                    Name = p.Name,
                    Items = earning.Select(r => new MarketplaceSync.Services.MercadoLibre.PromotionJoinItem
                    {
                        PublicationId = r.PublicationId,
                        Price = r.PriceEditable ? r.ProposedPrice : null,
                        OfferId = r.Offer.OfferId
                    }).ToList()
                };
                var action = new BotAction
                {
                    OrganizationId = org,
                    Kind = BotActionKinds.PromotionJoin,
                    Description = $"Entrar a «{p.Name}» con {earning.Count} {(earning.Count == 1 ? "publicación" : "publicaciones")}",
                    Payload = System.Text.Json.JsonSerializer.Serialize(payload),
                    Source = "promotions"
                };
                if (action.Payload.Length <= 4000)
                {
                    // Una sola tarjeta vigente por campaña: la nueva reemplaza a las anteriores.
                    var marker = $"\"PromotionId\":\"{p.Id}\"";
                    await _context.BotActions
                        .Where(a => a.OrganizationId == org && a.Kind == BotActionKinds.PromotionJoin &&
                                    a.Status == BotActionStatus.Pending && a.Payload != null && a.Payload.Contains(marker))
                        .ExecuteUpdateAsync(u => u
                            .SetProperty(a => a.Status, BotActionStatus.Expired)
                            .SetProperty(a => a.DecidedAt, DateTime.UtcNow), cancellationToken);
                    _context.BotActions.Add(action);
                    await _context.SaveChangesAsync(cancellationToken);
                    actionId = action.Id;
                    buttons.Add(new[] { new TelegramButton($"✅ Entrar con {(earning.Count == 1 ? "la que gana" : $"las {earning.Count} que ganan")}", CallbackData: $"ax:{action.Id}") });
                }
            }
            var second = new List<TelegramButton>();
            if (settings.PublicBaseUrl is not null)
                second.Add(new TelegramButton("Revisar en la app", Url: AppUrl(settings, "/Promotions")));
            if (actionId is { } id)
                second.Add(new TelegramButton("No", CallbackData: $"an:{id}"));
            if (second.Count > 0)
                buttons.Add(second);

            var messageId = await _telegram.SendMessageAsync(token, chatId, html.ToString().TrimEnd(), buttons, cancellationToken: cancellationToken);
            if (actionId is { } aid)
                await _context.BotActions.Where(a => a.Id == aid).ExecuteUpdateAsync(u => u.SetProperty(a => a.TelegramMessageId, messageId), cancellationToken);
            if (!await _context.NotificationLogs.AnyAsync(l => l.OrganizationId == org && l.Key == key, cancellationToken))
                await LogAsync(org, key, "promo", p.Id, chatId, messageId, cancellationToken);
            sent++;
        }

        if (force)
        {
            var discounted = await _context.MarketplacePublications.AsNoTracking()
                .Where(x => x.Product.OrganizationId == org && x.PromotionType != null)
                .Select(x => new { x.Product.Title, x.PromotionPrice, x.PromotionOriginalPrice, x.PromotionEndsAt })
                .ToListAsync(cancellationToken);
            var text = new StringBuilder();
            if (sent == 0)
                text.Append("Mercado Libre no te ha invitado a campañas con tus publicaciones.\n");
            if (discounted.Count > 0)
            {
                text.Append("\n🏷️ <b>Con promoción ahora</b> (").Append(discounted.Count).Append("):\n");
                foreach (var d in discounted.Take(10))
                    text.Append("• ").Append(E(Short(d.Title, 45))).Append(": <b>").Append(d.PromotionPrice is { } pp ? Money(pp) : "—").Append("</b>")
                        .Append(d.PromotionOriginalPrice is { } op ? " (antes " + Money(op) + ")" : "")
                        .Append(d.PromotionEndsAt is { } end ? " hasta el " + end.AddHours(-7).ToString("d MMM", MxCulture) : "").Append('\n');
            }
            else
            {
                text.Append("Ninguna publicación tiene descuento. Pídeme uno: «ponle 10% al faro por una semana».");
            }
            await _telegram.SendMessageAsync(token, chatId, text.ToString().Trim(),
                settings.PublicBaseUrl is null ? null : new[] { new[] { new TelegramButton("Abrir Promociones", Url: AppUrl(settings, "/Promotions")) } },
                cancellationToken: cancellationToken);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Facturas
    // ─────────────────────────────────────────────────────────────

    /// <summary>Compradores que pidieron factura (dieron RFC) y recordatorio de la factura global al empezar el mes.</summary>
    private async Task RunInvoicingAlertsAsync(string token, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var org = settings.OrganizationId;
        var chatId = settings.TelegramChatId!.Value;
        var invoicing = Service<MarketplaceSync.Services.Invoicing.InvoicingService>();
        var invoiceSettings = await invoicing.GetSettingsAsync(org, cancellationToken);
        if (!invoiceSettings.NotifyTelegram)
            return;
        var ready = invoicing.MissingSetup(invoiceSettings).Count == 0;

        List<MarketplaceSync.Web.Models.Orders.SalesOrder> requests;
        try
        {
            requests = await invoicing.CheckBuyerRequestsAsync(org, 15, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Checking invoice requests for {Org} failed.", org);
            requests = new();
        }

        foreach (var order in requests)
        {
            var key = "invreq:" + (order.ExternalPackId ?? order.ExternalOrderId);
            if (await _context.NotificationLogs.AnyAsync(l => l.OrganizationId == org && l.Key == key, cancellationToken))
                continue;
            var draft = await invoicing.DraftAsync(org, order.Id, readBuyer: false, cancellationToken);
            var html = new StringBuilder("🧾 <b>Te pidieron factura</b>\n")
                .Append("Venta #").Append(E(order.ExternalOrderId)).Append(" · ").Append(Money(draft?.Total ?? order.TotalAmount));
            if (draft is { Lines.Count: > 0 })
                html.Append("\n").Append(E(Short(string.Join(", ", draft.Lines.Select(l => $"{l.Quantity}× {l.Title}")), 120)));
            html.Append("\nRFC <b>").Append(E(order.BuyerRfc)).Append("</b>");

            var buttons = new List<IReadOnlyList<TelegramButton>>();
            long? actionId = null;
            if (ready)
            {
                var action = new BotAction
                {
                    OrganizationId = org,
                    Kind = BotActionKinds.Invoice,
                    Description = $"Timbrar la factura de la venta #{order.ExternalOrderId} para {order.BuyerRfc}",
                    Payload = System.Text.Json.JsonSerializer.Serialize(new { orderId = order.Id }),
                    Source = "invoicing"
                };
                _context.BotActions.Add(action);
                await _context.SaveChangesAsync(cancellationToken);
                actionId = action.Id;
                buttons.Add(new[] { new TelegramButton("🧾 Timbrar con sus datos", CallbackData: $"ax:{action.Id}") });
                html.Append("\n<i>Se timbra con los datos que capturó en Mercado Libre y se sube a su compra.</i>");
            }
            else
            {
                html.Append("\n<i>Para facturar desde aquí, completa tus datos fiscales y tu CSD en la app.</i>");
            }
            if (settings.PublicBaseUrl is not null)
                buttons.Add(new[] { new TelegramButton(ready ? "Revisar en la app" : "Configurar facturación", Url: AppUrl(settings, ready ? $"/Orders/Details/{order.Id}#factura" : "/Invoices/Settings")) });

            var messageId = await _telegram.SendMessageAsync(token, chatId, html.ToString(), buttons, cancellationToken: cancellationToken);
            if (actionId is { } aid)
                await _context.BotActions.Where(a => a.Id == aid).ExecuteUpdateAsync(u => u.SetProperty(a => a.TelegramMessageId, messageId), cancellationToken);
            await LogAsync(org, key, "invoice", order.Id.ToString(CultureInfo.InvariantCulture), chatId, messageId, cancellationToken);
        }

        // Recordatorio de la global del mes anterior (días 1 a 5, de 9 a 20 h).
        var now = LocalNow;
        if (now.Day <= 5 && now.Hour >= 9 && now.Hour < 20)
        {
            var previous = new DateTime(now.Year, now.Month, 1).AddMonths(-1);
            var key = $"global:{previous:yyyy-MM}";
            if (!await _context.NotificationLogs.AnyAsync(l => l.OrganizationId == org && l.Key == key, cancellationToken))
            {
                var preview = await invoicing.GlobalPreviewAsync(org, previous.Year, previous.Month, cancellationToken);
                if (preview.Existing is null && preview.Orders.Count > 0)
                    await SendGlobalCardAsync(token, settings, preview, ready, key, cancellationToken);
                else
                    await LogAsync(org, key, "global", null, chatId, 0, cancellationToken);
            }
        }
    }

    private async Task SendGlobalCardAsync(string token, NotificationSettings settings, MarketplaceSync.Services.Invoicing.GlobalPreview preview, bool ready, string? logKey, CancellationToken cancellationToken)
    {
        var org = settings.OrganizationId;
        var chatId = settings.TelegramChatId!.Value;
        var monthName = new DateTime(preview.Year, preview.Month, 1).ToString("MMMM yyyy", MxCulture);
        var html = new StringBuilder($"🧾 <b>Factura global de {monthName}</b>\n")
            .Append(preview.Orders.Count).Append(preview.Orders.Count == 1 ? " venta sin factura por " : " ventas sin factura por ")
            .Append("<b>").Append(preview.Total.ToString("C2", MxCulture)).Append("</b> (IVA incluido), al público en general.");
        if (preview.WithBuyerRfc.Count > 0)
            html.Append("\n⚠️ ").Append(preview.WithBuyerRfc.Count).Append(preview.WithBuyerRfc.Count == 1 ? " comprador te dio RFC: factúrala aparte (no va en la global)." : " compradores te dieron RFC: factúralas aparte (no van en la global).");

        var buttons = new List<IReadOnlyList<TelegramButton>>();
        long? actionId = null;
        if (ready)
        {
            var action = new BotAction
            {
                OrganizationId = org,
                Kind = BotActionKinds.GlobalInvoice,
                Description = $"Emitir la factura global de {monthName} ({preview.Orders.Count} ventas, {preview.Total.ToString("C2", MxCulture)})",
                Payload = System.Text.Json.JsonSerializer.Serialize(new { year = preview.Year, month = preview.Month }),
                Source = "invoicing"
            };
            _context.BotActions.Add(action);
            await _context.SaveChangesAsync(cancellationToken);
            actionId = action.Id;
            buttons.Add(new[] { new TelegramButton("🧾 Emitir factura global", CallbackData: $"ax:{action.Id}") });
        }
        if (settings.PublicBaseUrl is not null)
            buttons.Add(new[] { new TelegramButton(ready ? "Revisar en la app" : "Configurar facturación", Url: AppUrl(settings, ready ? $"/Invoices?year={preview.Year}&month={preview.Month}" : "/Invoices/Settings")) });

        var messageId = await _telegram.SendMessageAsync(token, chatId, html.ToString(), buttons, cancellationToken: cancellationToken);
        if (actionId is { } aid)
            await _context.BotActions.Where(a => a.Id == aid).ExecuteUpdateAsync(u => u.SetProperty(a => a.TelegramMessageId, messageId), cancellationToken);
        if (logKey is not null)
            await LogAsync(org, logKey, "global", null, chatId, messageId, cancellationToken);
    }

    /// <summary>/facturas: pedidos de factura pendientes y la global del mes pasado.</summary>
    private async Task SendInvoicesStatusAsync(string token, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var org = settings.OrganizationId;
        var chatId = settings.TelegramChatId!.Value;
        var invoicing = Service<MarketplaceSync.Services.Invoicing.InvoicingService>();
        var invoiceSettings = await invoicing.GetSettingsAsync(org, cancellationToken);
        var missing = invoicing.MissingSetup(invoiceSettings);
        await invoicing.CheckBuyerRequestsAsync(org, 15, cancellationToken);

        var since = DateTime.UtcNow.AddDays(-30);
        var pending = await _context.SalesOrders.AsNoTracking()
            .Where(o => o.OrganizationId == org && o.Status == "paid" && o.BuyerRfc != null && o.SalesInvoiceId == null && o.DateCreated >= since)
            .OrderByDescending(o => o.DateCreated)
            .Take(10)
            .ToListAsync(cancellationToken);
        var monthStart = new DateTime(LocalNow.Year, LocalNow.Month, 1);
        var (from, _) = MarketplaceSync.Services.Invoicing.InvoicingService.MonthRange(monthStart.Year, monthStart.Month);
        var issued = await _context.SalesInvoices.AsNoTracking()
            .Where(i => i.OrganizationId == org && i.IssuedAt >= from && i.Status == MarketplaceSync.Web.Models.Invoicing.InvoiceStatus.Issued)
            .ToListAsync(cancellationToken);

        var html = new StringBuilder("🧾 <b>Facturas</b>\n");
        if (missing.Count > 0)
            html.Append("Para facturar falta: ").Append(E(string.Join(", ", missing))).Append(".\n");
        html.Append("Este mes: ").Append(issued.Count).Append(issued.Count == 1 ? " factura" : " facturas").Append(" por ").Append(issued.Sum(i => i.Total).ToString("C2", MxCulture)).Append('\n');
        if (pending.Count == 0)
            html.Append("Nadie está esperando factura.");
        else
        {
            html.Append("\n<b>Te pidieron factura</b> (").Append(pending.Count).Append("):\n");
            foreach (var o in pending)
                html.Append("• #").Append(E(o.ExternalOrderId)).Append(" · ").Append(E(o.BuyerRfc)).Append(" · ").Append(Money(o.TotalAmount)).Append('\n');
        }
        await _telegram.SendMessageAsync(token, chatId, html.ToString().Trim(),
            settings.PublicBaseUrl is null ? null : new[] { new[] { new TelegramButton("Abrir Facturas", Url: AppUrl(settings, "/Invoices")) } },
            cancellationToken: cancellationToken);

        var previous = monthStart.AddMonths(-1);
        var preview = await invoicing.GlobalPreviewAsync(org, previous.Year, previous.Month, cancellationToken);
        if (preview.Existing is null && preview.Orders.Count > 0)
            await SendGlobalCardAsync(token, settings, preview, missing.Count == 0, null, cancellationToken);
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
