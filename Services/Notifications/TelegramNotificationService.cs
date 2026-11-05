using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MarketplaceSync.Services.Inventory;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Notifications;
using MarketplaceSync.Web.Models.Purchasing;
using MarketplaceSync.Web.Models.Questions;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Notifications;

public sealed record TelegramStatus(
    bool BotConnected,
    bool TokenFromEnvironment,
    string? BotUsername,
    bool ChatLinked,
    string? ChatName,
    string? LinkUrl,
    NotificationSettings Settings);

/// <summary>
/// Avisos de la tienda por Telegram y acciones desde Telegram:
/// preguntas nuevas (con la respuesta sugerida y botones para enviarla), ventas, agotados y un resumen diario.
/// Responder a un aviso de pregunta con texto envía esa respuesta a Mercado Libre.
/// No usa webhook: un trabajo de Hangfire lee los mensajes del bot cada minuto (getUpdates).
/// </summary>
public sealed class TelegramNotificationService
{
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    private readonly AppDbContext _context;
    private readonly TelegramClient _telegram;
    private readonly IMercadoLibreQuestionService _questions;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TelegramNotificationService> _logger;

    public TelegramNotificationService(
        AppDbContext context,
        TelegramClient telegram,
        IMercadoLibreQuestionService questions,
        IConfiguration configuration,
        ILogger<TelegramNotificationService> logger)
    {
        _context = context;
        _telegram = telegram;
        _questions = questions;
        _configuration = configuration;
        _logger = logger;
    }

    private string? EnvironmentToken => _configuration["Telegram:BotToken"] is { Length: > 0 } t ? t.Trim() : null;

    private string? TokenFor(NotificationSettings settings) => EnvironmentToken ?? settings.TelegramBotToken;

    private static DateTime LocalNow => DateTime.UtcNow.AddHours(-7);

    // ─────────────────────────────────────────────────────────────
    // Configuración
    // ─────────────────────────────────────────────────────────────

    public async Task<NotificationSettings> GetOrCreateSettingsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var settings = await _context.NotificationSettings.FirstOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        if (settings is not null)
            return settings;

        settings = new NotificationSettings { OrganizationId = organizationId };
        _context.NotificationSettings.Add(settings);
        await _context.SaveChangesAsync(cancellationToken);
        return settings;
    }

    public async Task<TelegramStatus> GetStatusAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateSettingsAsync(organizationId, cancellationToken);
        var token = TokenFor(settings);

        // Con el token en Render basta con conocer el nombre del bot una vez.
        if (token is not null && string.IsNullOrWhiteSpace(settings.TelegramBotUsername))
        {
            try
            {
                var me = await _telegram.GetMeAsync(token, cancellationToken);
                settings.TelegramBotUsername = me.Username;
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Telegram getMe failed.");
            }
        }

        if (token is not null && settings.TelegramChatId is null && string.IsNullOrWhiteSpace(settings.LinkCode))
        {
            settings.LinkCode = NewLinkCode();
            await _context.SaveChangesAsync(cancellationToken);
        }

        var linkUrl = token is not null && settings.TelegramChatId is null && !string.IsNullOrWhiteSpace(settings.TelegramBotUsername)
            ? $"https://t.me/{settings.TelegramBotUsername}?start={settings.LinkCode}"
            : null;

        return new TelegramStatus(
            token is not null,
            EnvironmentToken is not null,
            settings.TelegramBotUsername,
            settings.TelegramChatId is not null,
            settings.TelegramChatName,
            linkUrl,
            settings);
    }

    /// <summary>Valida el token con Telegram y lo guarda. Devuelve el nombre del bot.</summary>
    public async Task<string> ConnectBotAsync(Guid organizationId, string? token, string? publicBaseUrl, CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateSettingsAsync(organizationId, cancellationToken);
        var effective = EnvironmentToken ?? token?.Trim();
        if (!TelegramClient.LooksLikeToken(effective))
            throw new ArgumentException("Ese no parece un token de Telegram. Cópialo completo del mensaje de @BotFather (números, dos puntos y letras).");

        TelegramBotInfo me;
        try
        {
            me = await _telegram.GetMeAsync(effective!, cancellationToken);
            await _telegram.DeleteWebhookAsync(effective!, cancellationToken);
        }
        catch (TelegramApiException exception) when (exception.IsUnauthorizedOrBlocked)
        {
            throw new ArgumentException("Telegram no aceptó ese token. Revisa que esté completo o genera uno nuevo con /token en @BotFather.");
        }

        if (EnvironmentToken is null)
            settings.TelegramBotToken = effective;
        if (settings.TelegramBotUsername != me.Username)
        {
            // Bot distinto: hay que volver a vincular el chat.
            settings.TelegramChatId = null;
            settings.TelegramChatName = null;
            settings.LinkedAt = null;
            settings.UpdateOffset = 0;
        }
        settings.TelegramBotUsername = me.Username;
        settings.LinkCode = settings.TelegramChatId is null ? NewLinkCode() : settings.LinkCode;
        settings.PublicBaseUrl = publicBaseUrl ?? settings.PublicBaseUrl;
        settings.LastError = null;
        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return me.Username;
    }

    public async Task SavePreferencesAsync(
        Guid organizationId, bool questions, bool orders, bool stock, bool summary, int summaryHour, string? publicBaseUrl,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateSettingsAsync(organizationId, cancellationToken);
        settings.NotifyQuestions = questions;
        settings.NotifyOrders = orders;
        settings.NotifyStock = stock;
        settings.DailySummary = summary;
        settings.DailySummaryHour = Math.Clamp(summaryHour, 0, 23);
        settings.PublicBaseUrl = publicBaseUrl ?? settings.PublicBaseUrl;
        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DisconnectAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateSettingsAsync(organizationId, cancellationToken);
        settings.TelegramChatId = null;
        settings.TelegramChatName = null;
        settings.LinkedAt = null;
        settings.TelegramBotToken = null;
        settings.TelegramBotUsername = EnvironmentToken is null ? null : settings.TelegramBotUsername;
        settings.LinkCode = null;
        settings.UpdateOffset = 0;
        settings.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task SendTestAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateSettingsAsync(organizationId, cancellationToken);
        var token = TokenFor(settings) ?? throw new InvalidOperationException("Primero conecta tu bot.");
        if (settings.TelegramChatId is not { } chatId)
            throw new InvalidOperationException("Primero abre tu bot en Telegram y pulsa Iniciar.");

        await _telegram.SendMessageAsync(token, chatId,
            "✅ <b>Prueba de MarketplaceSync</b>\nAsí te llegarán los avisos de tu tienda.",
            new[] { new[] { new TelegramButton("Abrir la app", Url: AppUrl(settings, "/")) } }.Where(_ => settings.PublicBaseUrl is not null).ToList(),
            cancellationToken: cancellationToken);
    }

    // ─────────────────────────────────────────────────────────────
    // Leer mensajes del bot
    // ─────────────────────────────────────────────────────────────

    /// <summary>Lee los mensajes de todos los bots conectados (un bot puede servir a varias organizaciones).</summary>
    public async Task PollAllAsync(CancellationToken cancellationToken = default)
    {
        var hasEnvironmentToken = EnvironmentToken is not null;
        var all = await _context.NotificationSettings
            .Where(x => x.TelegramBotToken != null || hasEnvironmentToken)
            .ToListAsync(cancellationToken);

        foreach (var group in all.Where(x => TokenFor(x) is not null).GroupBy(x => TokenFor(x)!))
        {
            try
            {
                await PollTokenAsync(group.Key, group.ToList(), cancellationToken);
            }
            catch (TelegramApiException exception) when (exception.IsUnauthorizedOrBlocked)
            {
                foreach (var s in group)
                {
                    s.LastError = "Telegram rechazó el token del bot. Vuelve a conectarlo.";
                    s.LastErrorAt = DateTime.UtcNow;
                }
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Telegram polling failed.");
            }
        }
    }

    public async Task PollOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var settings = await GetOrCreateSettingsAsync(organizationId, cancellationToken);
        var token = TokenFor(settings);
        if (token is null)
            return;
        var sameBot = await _context.NotificationSettings.ToListAsync(cancellationToken);
        await PollTokenAsync(token, sameBot.Where(x => TokenFor(x) == token).ToList(), cancellationToken);
    }

    private async Task PollTokenAsync(string token, List<NotificationSettings> orgs, CancellationToken cancellationToken)
    {
        var orgIds = orgs.Select(x => x.Id).ToList();
        var offset = await _context.NotificationSettings.AsNoTracking()
            .Where(x => orgIds.Contains(x.Id))
            .MaxAsync(x => (long?)x.UpdateOffset, cancellationToken) ?? 0;
        var updates = await _telegram.GetUpdatesAsync(token, offset + 1, cancellationToken);
        if (updates.Count == 0)
            return;

        foreach (var update in updates)
        {
            try
            {
                await HandleUpdateAsync(token, orgs, update, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Telegram update {UpdateId} failed.", update.UpdateId);
            }

            // Se guarda directo en la base: así un mensaje nunca se procesa dos veces.
            var ids = orgs.Select(x => x.Id).ToList();
            var updateId = update.UpdateId;
            await _context.NotificationSettings
                .Where(x => ids.Contains(x.Id) && x.UpdateOffset < updateId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.UpdateOffset, updateId), cancellationToken);
            foreach (var s in orgs)
                s.UpdateOffset = Math.Max(s.UpdateOffset, updateId);
        }
    }

    private async Task HandleUpdateAsync(string token, List<NotificationSettings> orgs, TelegramIncoming update, CancellationToken cancellationToken)
    {
        if (update.ChatId == 0)
            return;

        var text = update.Text?.Trim() ?? string.Empty;

        // Vincular: /start CODIGO
        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            var code = text.Length > 6 ? text[6..].Trim() : string.Empty;
            var target = orgs.FirstOrDefault(x => !string.IsNullOrEmpty(code) && x.LinkCode == code);
            if (target is null)
            {
                var already = orgs.FirstOrDefault(x => x.TelegramChatId == update.ChatId);
                await _telegram.SendMessageAsync(token, update.ChatId, already is not null
                    ? "Ya estás vinculado. Escribe /ayuda para ver lo que puedo hacer."
                    : "Este bot es privado. Vincúlalo desde MarketplaceSync → <b>Avisos por Telegram</b>.",
                    cancellationToken: cancellationToken);
                return;
            }

            target.TelegramChatId = update.ChatId;
            target.TelegramChatName = update.ChatName;
            target.LinkedAt = DateTime.UtcNow;
            target.LinkCode = null;
            target.LastError = null;
            // El primer resumen llega la mañana siguiente, no al vincular.
            target.LastDailySummaryOn = DateOnly.FromDateTime(LocalNow);
            await _context.SaveChangesAsync(cancellationToken);
            await BaselineAsync(target.OrganizationId, cancellationToken);

            var pending = await _context.BuyerQuestions.CountAsync(
                q => q.OrganizationId == target.OrganizationId && q.Status == BuyerQuestionStatus.Unanswered, cancellationToken);
            await _telegram.SendMessageAsync(token, update.ChatId,
                "✅ <b>Listo, ya estás vinculado a MarketplaceSync.</b>\n\n" +
                "Te avisaré de preguntas nuevas, ventas, piezas agotadas y te mandaré un resumen cada mañana.\n" +
                "Para contestar una pregunta: pulsa <b>Enviar respuesta</b> o <b>responde al mensaje</b> con tu texto.\n\n" +
                (pending > 0 ? $"Tienes <b>{pending}</b> {(pending == 1 ? "pregunta" : "preguntas")} sin responder: escribe /pendientes para verlas.\n" : "") +
                "Escribe /ayuda cuando quieras.",
                cancellationToken: cancellationToken);
            return;
        }

        var settings = orgs.FirstOrDefault(x => x.TelegramChatId == update.ChatId);
        if (settings is null)
        {
            if (update.CallbackId is not null)
                await _telegram.AnswerCallbackAsync(token, update.CallbackId, "Este chat no está vinculado.", cancellationToken);
            return; // Ignora a desconocidos: el bot es privado.
        }

        var org = settings.OrganizationId;

        // Botones
        if (update.CallbackId is not null)
        {
            await HandleCallbackAsync(token, settings, update, cancellationToken);
            return;
        }

        // Respuesta a un aviso de pregunta → contestar en Mercado Libre
        if (update.ReplyToMessageId is { } replyTo && !text.StartsWith('/'))
        {
            var log = await _context.NotificationLogs.AsNoTracking()
                .Where(x => x.OrganizationId == org && x.TelegramChatId == update.ChatId &&
                            x.TelegramMessageId == replyTo && x.Kind == NotificationKind.Question)
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (log is not null && long.TryParse(log.EntityId, out var questionId))
            {
                await AnswerAndReportAsync(token, settings, questionId, text, replyTo, update.MessageId, cancellationToken);
                return;
            }
        }

        switch (text.Split(' ', 2)[0].ToLowerInvariant())
        {
            case "/pendientes":
            case "/preguntas":
                await SendPendingQuestionsAsync(token, settings, cancellationToken);
                break;
            case "/resumen":
                await SendSummaryAsync(token, settings, force: true, cancellationToken);
                break;
            default:
                await _telegram.SendMessageAsync(token, update.ChatId,
                    "<b>Lo que puedo hacer</b>\n" +
                    "/pendientes — preguntas sin responder, con su respuesta sugerida\n" +
                    "/resumen — ventas de ayer y lo pendiente de hoy\n\n" +
                    "Para contestar una pregunta, <b>responde a su mensaje</b> con tu texto o usa sus botones.",
                    cancellationToken: cancellationToken);
                break;
        }
    }

    private async Task HandleCallbackAsync(string token, NotificationSettings settings, TelegramIncoming update, CancellationToken cancellationToken)
    {
        var data = update.CallbackData ?? string.Empty;
        var parts = data.Split(':');
        if (parts.Length != 2 || !long.TryParse(parts[1], out var questionId))
        {
            await _telegram.AnswerCallbackAsync(token, update.CallbackId!, null, cancellationToken);
            return;
        }

        var question = await _context.BuyerQuestions.AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == questionId && q.OrganizationId == settings.OrganizationId, cancellationToken);
        if (question is null)
        {
            await _telegram.AnswerCallbackAsync(token, update.CallbackId!, "Ya no encontré esa pregunta.", cancellationToken);
            return;
        }

        switch (parts[0])
        {
            case "qs": // enviar la sugerida
                if (string.IsNullOrWhiteSpace(question.SuggestedAnswer))
                {
                    await _telegram.AnswerCallbackAsync(token, update.CallbackId!, "No hay respuesta sugerida. Responde al mensaje con tu texto.", cancellationToken);
                    return;
                }
                await _telegram.AnswerCallbackAsync(token, update.CallbackId!, "Enviando…", cancellationToken);
                await AnswerAndReportAsync(token, settings, questionId, question.SuggestedAnswer, update.CallbackMessageId, null, cancellationToken);
                break;

            case "qr": // otra sugerencia
                await _telegram.AnswerCallbackAsync(token, update.CallbackId!, "Pidiendo otra respuesta a la IA…", cancellationToken);
                var suggestion = await _questions.SuggestAsync(settings.OrganizationId, questionId, cancellationToken);
                var refreshed = await LoadQuestionCardAsync(settings.OrganizationId, questionId, cancellationToken);
                if (refreshed is not null && update.CallbackMessageId is { } messageId)
                {
                    var (html, buttons) = QuestionCard(settings, refreshed.Value.Question, refreshed.Value.Title, suggestion is null ? "La IA no pudo sugerir otra; responde al mensaje con tu texto." : null);
                    await _telegram.EditMessageAsync(token, update.ChatId, messageId, html, buttons, cancellationToken);
                }
                break;

            default:
                await _telegram.AnswerCallbackAsync(token, update.CallbackId!, null, cancellationToken);
                break;
        }
    }

    private async Task AnswerAndReportAsync(
        string token, NotificationSettings settings, long questionId, string text,
        long? cardMessageId, long? userMessageId, CancellationToken cancellationToken)
    {
        var chatId = settings.TelegramChatId!.Value;
        try
        {
            await _questions.AnswerAsync(settings.OrganizationId, questionId, text, cancellationToken);
            var card = await LoadQuestionCardAsync(settings.OrganizationId, questionId, cancellationToken);
            if (card is not null && cardMessageId is { } messageId)
            {
                var html = $"✅ <b>Respondida</b> · {E(card.Value.Title)}\n«{E(card.Value.Question.Text)}»\n\n<b>Tu respuesta:</b> {E(card.Value.Question.AnswerText ?? text)}";
                await _telegram.EditMessageAsync(token, chatId, messageId, html, null, cancellationToken);
            }
            if (userMessageId is not null || cardMessageId is null)
                await _telegram.SendMessageAsync(token, chatId, "✅ Respuesta enviada a Mercado Libre.", replyToMessageId: userMessageId, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            await _telegram.SendMessageAsync(token, chatId, "⚠️ " + E(exception.Message), replyToMessageId: userMessageId ?? cardMessageId, cancellationToken: cancellationToken);
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Avisos automáticos
    // ─────────────────────────────────────────────────────────────

    public async Task SweepAllAsync(CancellationToken cancellationToken = default)
    {
        var orgIds = await _context.NotificationSettings.AsNoTracking()
            .Where(x => x.TelegramChatId != null)
            .Select(x => x.OrganizationId)
            .ToListAsync(cancellationToken);
        foreach (var org in orgIds)
        {
            try
            {
                await SweepAsync(org, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Notification sweep failed for {OrganizationId}.", org);
            }
            finally
            {
                _context.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Manda lo que no se ha avisado: preguntas, ventas, agotados y el resumen del día.</summary>
    public async Task SweepAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var settings = await _context.NotificationSettings.FirstOrDefaultAsync(x => x.OrganizationId == organizationId, cancellationToken);
        var token = settings is null ? null : TokenFor(settings);
        if (settings?.TelegramChatId is not { } chatId || token is null)
            return;

        var since = settings.LinkedAt ?? DateTime.UtcNow;
        try
        {
            if (settings.NotifyQuestions)
                await NotifyQuestionsAsync(token, settings, since, cancellationToken);
            if (settings.NotifyOrders)
                await NotifyOrdersAsync(token, settings, since, cancellationToken);
            if (settings.NotifyStock)
                await NotifyOutOfStockAsync(token, settings, since, cancellationToken);
            if (settings.DailySummary)
                await SendSummaryAsync(token, settings, force: false, cancellationToken);
        }
        catch (TelegramApiException exception) when (exception.IsUnauthorizedOrBlocked)
        {
            settings.LastError = "Telegram no dejó enviar el aviso: ¿bloqueaste el bot? Ábrelo y pulsa Iniciar otra vez.";
            settings.LastErrorAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task NotifyQuestionsAsync(string token, NotificationSettings settings, DateTime since, CancellationToken cancellationToken)
    {
        var org = settings.OrganizationId;
        var sent = _context.NotificationLogs.Where(l => l.OrganizationId == org && l.Kind == NotificationKind.Question).Select(l => l.Key);
        var pending = await _context.BuyerQuestions.AsNoTracking()
            .Where(q => q.OrganizationId == org && q.Status == BuyerQuestionStatus.Unanswered &&
                        q.SyncedAt >= since && !sent.Contains("question:" + q.Id))
            .OrderBy(q => q.DateCreated)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var question in pending)
        {
            var card = await LoadQuestionCardAsync(org, question.Id, cancellationToken);
            if (card is null) continue;
            var (html, buttons) = QuestionCard(settings, card.Value.Question, card.Value.Title, null);
            var messageId = await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value, html, buttons, cancellationToken: cancellationToken);
            await LogAsync(org, "question:" + question.Id, NotificationKind.Question, question.Id.ToString(CultureInfo.InvariantCulture), settings.TelegramChatId, messageId, cancellationToken);
        }
    }

    private async Task SendPendingQuestionsAsync(string token, NotificationSettings settings, CancellationToken cancellationToken)
    {
        var org = settings.OrganizationId;
        var pending = await _context.BuyerQuestions.AsNoTracking()
            .Where(q => q.OrganizationId == org && q.Status == BuyerQuestionStatus.Unanswered)
            .OrderBy(q => q.DateCreated)
            .Take(5)
            .Select(q => q.Id)
            .ToListAsync(cancellationToken);
        var total = await _context.BuyerQuestions.CountAsync(q => q.OrganizationId == org && q.Status == BuyerQuestionStatus.Unanswered, cancellationToken);

        if (pending.Count == 0)
        {
            await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value, "✅ No tienes preguntas sin responder.", cancellationToken: cancellationToken);
            return;
        }

        foreach (var id in pending)
        {
            var card = await LoadQuestionCardAsync(org, id, cancellationToken);
            if (card is null) continue;
            var (html, buttons) = QuestionCard(settings, card.Value.Question, card.Value.Title, null);
            var messageId = await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value, html, buttons, cancellationToken: cancellationToken);
            // Otro registro para que también se pueda responder a este mensaje.
            await LogAsync(org, $"question:{id}:msg:{messageId}", NotificationKind.Question, id.ToString(CultureInfo.InvariantCulture), settings.TelegramChatId, messageId, cancellationToken);
        }
        if (total > pending.Count)
            await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value, $"…y {total - pending.Count} más en la app.", cancellationToken: cancellationToken);
    }

    private async Task NotifyOrdersAsync(string token, NotificationSettings settings, DateTime since, CancellationToken cancellationToken)
    {
        var org = settings.OrganizationId;
        var sent = _context.NotificationLogs.Where(l => l.OrganizationId == org && l.Kind == NotificationKind.Order).Select(l => l.Key);
        var orders = await _context.SalesOrders.AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.OrganizationId == org && o.Status == "paid" && o.SyncedAt >= since &&
                        !sent.Contains("order:" + o.Id))
            .OrderBy(o => o.DateCreated)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var order in orders)
        {
            var lines = new StringBuilder();
            lines.Append("🛒 <b>Venta nueva</b> · ").Append(E(order.TotalAmount.ToString("C0", Mx))).Append('\n');
            foreach (var item in order.Items.Take(5))
            {
                lines.Append(item.Quantity).Append("× ").Append(E(item.Title ?? item.ExternalItemId));
                if (!string.IsNullOrWhiteSpace(item.SellerSku))
                {
                    var stock = await _context.ProductVariants.AsNoTracking()
                        .Where(v => v.OrganizationId == org && v.Sku == item.SellerSku)
                        .Select(v => (int?)v.Stock)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (stock.HasValue)
                        lines.Append(stock <= 0 ? " — <b>se agotó</b>" : $" — quedan {stock}");
                }
                lines.Append('\n');
            }
            lines.Append("Pedido #").Append(E(order.ExternalOrderId));

            var buttons = settings.PublicBaseUrl is null
                ? null
                : new List<IReadOnlyList<TelegramButton>> { new[] { new TelegramButton("Ver pedido", Url: AppUrl(settings, $"/Orders/Details/{order.Id}")) } };
            var messageId = await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value, lines.ToString(), buttons, cancellationToken: cancellationToken);
            await LogAsync(org, "order:" + order.Id, NotificationKind.Order, order.Id.ToString(CultureInfo.InvariantCulture), settings.TelegramChatId, messageId, cancellationToken);
        }
    }

    private async Task NotifyOutOfStockAsync(string token, NotificationSettings settings, DateTime since, CancellationToken cancellationToken)
    {
        var org = settings.OrganizationId;
        var candidates = await _context.ProductVariants.AsNoTracking()
            .Where(v => v.OrganizationId == org && v.IsActive && v.Stock <= 0 && v.UpdatedAt >= since)
            .Select(v => new { v.Id, v.Sku, v.Stock, v.UpdatedAt, v.Product.Title })
            .Take(20)
            .ToListAsync(cancellationToken);

        foreach (var v in candidates)
        {
            var key = $"out:{v.Id}:{v.UpdatedAt:yyyyMMddHHmm}";
            if (await _context.NotificationLogs.AnyAsync(l => l.OrganizationId == org && l.Key == key, cancellationToken))
                continue;
            var html = $"⚠️ <b>{(v.Stock < 0 ? "Sobrevendido" : "Se agotó")}</b>: {E(v.Title ?? v.Sku)} ({E(v.Sku)})\n" +
                       "Mercado Libre ya no lo ofrece. Agrégalo a tu próxima compra en El Paso.";
            var buttons = settings.PublicBaseUrl is null
                ? null
                : new List<IReadOnlyList<TelegramButton>> { new[] { new TelegramButton("Qué resurtir", Url: AppUrl(settings, "/Purchasing?tab=restock")) } };
            var messageId = await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value, html, buttons, cancellationToken: cancellationToken);
            await LogAsync(org, key, NotificationKind.OutOfStock, v.Id.ToString(CultureInfo.InvariantCulture), settings.TelegramChatId, messageId, cancellationToken);
        }
    }

    private async Task SendSummaryAsync(string token, NotificationSettings settings, bool force, CancellationToken cancellationToken)
    {
        var local = LocalNow;
        var today = DateOnly.FromDateTime(local);
        if (!force && (local.Hour < settings.DailySummaryHour || settings.LastDailySummaryOn == today))
            return;

        var org = settings.OrganizationId;
        var yesterdayStartUtc = DateTime.SpecifyKind(today.AddDays(-1).ToDateTime(TimeOnly.MinValue).AddHours(7), DateTimeKind.Utc);
        var todayStartUtc = DateTime.SpecifyKind(today.ToDateTime(TimeOnly.MinValue).AddHours(7), DateTimeKind.Utc);

        var sales = await _context.SalesOrders.AsNoTracking()
            .Where(o => o.OrganizationId == org && o.Status == "paid" && o.DateCreated >= yesterdayStartUtc && o.DateCreated < todayStartUtc)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Total = g.Sum(o => o.TotalAmount) })
            .FirstOrDefaultAsync(cancellationToken);
        var questions = await _context.BuyerQuestions.CountAsync(q => q.OrganizationId == org && q.Status == BuyerQuestionStatus.Unanswered, cancellationToken);
        var lowStock = await _context.ProductVariants.CountAsync(v => v.OrganizationId == org && v.IsActive && v.Stock <= v.LowStockThreshold, cancellationToken);
        var expiryLimit = InventoryLotService.Today.AddDays(InventoryLotService.WarningDays);
        var lots = await _context.InventoryLots.CountAsync(x => x.OrganizationId == org && x.WrittenOffAt == null && x.QuantityRemaining > 0 && x.ExpiresOn <= expiryLimit, cancellationToken);
        var toBuy = await _context.PurchaseCandidates.CountAsync(x => x.OrganizationId == org && x.Status == PurchaseCandidateStatus.Suggested && x.DemandScore > 0, cancellationToken);
        var inTransit = await _context.PurchaseCandidates.CountAsync(x => x.OrganizationId == org && x.Status == PurchaseCandidateStatus.Purchased, cancellationToken);

        var html = new StringBuilder();
        html.Append(local.Hour < 12 ? "☀️ <b>Buenos días</b>\n" : "📋 <b>Resumen</b>\n");
        html.Append("Ayer: ").Append(sales is null ? "sin ventas" : $"<b>{sales.Count}</b> {(sales.Count == 1 ? "venta" : "ventas")} por <b>{E(sales.Total.ToString("C0", Mx))}</b>").Append("\n\n");
        var todo = new List<string>();
        if (questions > 0) todo.Add($"❓ {questions} {(questions == 1 ? "pregunta" : "preguntas")} sin responder (/pendientes)");
        if (lowStock > 0) todo.Add($"📦 {lowStock} SKU con stock bajo");
        if (lots > 0) todo.Add($"⏳ {lots} {(lots == 1 ? "lote" : "lotes")} por caducar");
        if (inTransit > 0) todo.Add($"🚚 {inTransit} {(inTransit == 1 ? "compra" : "compras")} en camino");
        if (toBuy > 0) todo.Add($"🛍️ {toBuy} {(toBuy == 1 ? "producto conviene" : "productos conviene")} comprar en El Paso");
        html.Append(todo.Count == 0 ? "Todo al día ✅" : "<b>Pendientes</b>\n" + string.Join("\n", todo));

        var buttons = settings.PublicBaseUrl is null
            ? null
            : new List<IReadOnlyList<TelegramButton>> { new[] { new TelegramButton("Abrir Hoy", Url: AppUrl(settings, "/")), new TelegramButton("Qué comprar", Url: AppUrl(settings, "/Purchasing")) } };
        var messageId = await _telegram.SendMessageAsync(token, settings.TelegramChatId!.Value, html.ToString(), buttons, cancellationToken: cancellationToken);

        if (!force)
        {
            settings.LastDailySummaryOn = today;
            await LogAsync(org, $"summary:{today:yyyyMMdd}", NotificationKind.Summary, null, settings.TelegramChatId, messageId, cancellationToken);
        }
    }

    /// <summary>Al vincular: lo que ya existía no se avisa de golpe (evita una lluvia de mensajes).</summary>
    private async Task BaselineAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var existingQuestions = await _context.BuyerQuestions.AsNoTracking()
            .Where(q => q.OrganizationId == organizationId && q.Status == BuyerQuestionStatus.Unanswered)
            .Select(q => q.Id).ToListAsync(cancellationToken);
        var recentOrders = await _context.SalesOrders.AsNoTracking()
            .Where(o => o.OrganizationId == organizationId && o.DateCreated >= DateTime.UtcNow.AddDays(-3))
            .Select(o => o.Id).ToListAsync(cancellationToken);
        var keys = existingQuestions.Select(id => ("question:" + id, NotificationKind.Question, id.ToString(CultureInfo.InvariantCulture)))
            .Concat(recentOrders.Select(id => ("order:" + id, NotificationKind.Order, id.ToString(CultureInfo.InvariantCulture))));
        foreach (var (key, kind, entity) in keys)
        {
            if (!await _context.NotificationLogs.AnyAsync(l => l.OrganizationId == organizationId && l.Key == key, cancellationToken))
                _context.NotificationLogs.Add(new NotificationLog { OrganizationId = organizationId, Key = key, Kind = kind, EntityId = entity });
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    // ─────────────────────────────────────────────────────────────
    // Utilidades
    // ─────────────────────────────────────────────────────────────

    private async Task<(BuyerQuestion Question, string Title)?> LoadQuestionCardAsync(Guid org, long questionId, CancellationToken cancellationToken)
    {
        var question = await _context.BuyerQuestions.AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == questionId && q.OrganizationId == org, cancellationToken);
        if (question is null)
            return null;
        var title = await _context.MarketplacePublications.AsNoTracking()
            .Where(p => p.ExternalItemId == question.ItemId && p.Product.OrganizationId == org)
            .Select(p => p.Product.Title)
            .FirstOrDefaultAsync(cancellationToken);
        return (question, title ?? question.ItemId);
    }

    private (string Html, List<IReadOnlyList<TelegramButton>> Buttons) QuestionCard(NotificationSettings settings, BuyerQuestion question, string title, string? note)
    {
        var minutes = (int)Math.Max(0, (DateTime.UtcNow - question.DateCreated).TotalMinutes);
        var ago = minutes < 60 ? $"hace {Math.Max(1, minutes)} min" : minutes < 1440 ? $"hace {minutes / 60} h" : $"hace {minutes / 1440} d";
        var html = new StringBuilder();
        html.Append("❓ <b>Pregunta nueva</b> · ").Append(ago).Append('\n');
        html.Append("<b>").Append(E(title)).Append("</b>\n");
        html.Append("«").Append(E(question.Text)).Append("»\n\n");
        if (!string.IsNullOrWhiteSpace(question.SuggestedAnswer))
            html.Append("💡 <b>Respuesta sugerida:</b>\n").Append(E(question.SuggestedAnswer)).Append("\n\n");
        if (note is not null)
            html.Append("<i>").Append(E(note)).Append("</i>\n\n");
        html.Append("<i>Responde a este mensaje para contestar con tu propio texto.</i>");

        var buttons = new List<IReadOnlyList<TelegramButton>>();
        var row = new List<TelegramButton>();
        if (!string.IsNullOrWhiteSpace(question.SuggestedAnswer))
            row.Add(new TelegramButton("✅ Enviar respuesta", CallbackData: $"qs:{question.Id}"));
        row.Add(new TelegramButton("🔄 Otra sugerencia", CallbackData: $"qr:{question.Id}"));
        buttons.Add(row);
        if (settings.PublicBaseUrl is not null)
            buttons.Add(new[] { new TelegramButton("Abrir en la app", Url: AppUrl(settings, $"/Questions#q{question.Id}")) });
        return (html.ToString(), buttons);
    }

    private async Task LogAsync(Guid org, string key, string kind, string? entityId, long? chatId, long messageId, CancellationToken cancellationToken)
    {
        _context.NotificationLogs.Add(new NotificationLog
        {
            OrganizationId = org,
            Key = key.Length > 200 ? key[..200] : key,
            Kind = kind,
            EntityId = entityId,
            TelegramChatId = chatId,
            TelegramMessageId = messageId,
            SentAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static string AppUrl(NotificationSettings settings, string path) =>
        (settings.PublicBaseUrl ?? string.Empty).TrimEnd('/') + path;

    /// <summary>Telegram (parse_mode HTML) solo necesita escapar &lt; &gt; y &amp;; los acentos van tal cual.</summary>
    private static string E(string? text) => (text ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string NewLinkCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
}
