using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarketplaceSync.Services.Notifications;

public sealed record TelegramBotInfo(long Id, string Username, string FirstName);

public sealed record TelegramButton(string Text, string? CallbackData = null, string? Url = null);

public sealed record TelegramIncoming(
    long UpdateId,
    long ChatId,
    string? ChatName,
    long? MessageId,
    string? Text,
    long? ReplyToMessageId,
    string? CallbackId,
    string? CallbackData,
    long? CallbackMessageId);

/// <summary>
/// Llamadas mínimas a la API de bots de Telegram (https://core.telegram.org/bots/api).
/// Base configurable con Telegram:ApiBaseUrl para pruebas.
/// </summary>
public sealed class TelegramClient
{
    public const string HttpClientName = "telegram";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiBase;

    public TelegramClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        var configured = configuration["Telegram:ApiBaseUrl"];
        _apiBase = string.IsNullOrWhiteSpace(configured) ? "https://api.telegram.org" : configured.TrimEnd('/');
    }

    public static bool LooksLikeToken(string? token) =>
        !string.IsNullOrWhiteSpace(token) &&
        System.Text.RegularExpressions.Regex.IsMatch(token.Trim(), @"^\d{5,15}:[A-Za-z0-9_-]{30,60}$");

    public async Task<TelegramBotInfo> GetMeAsync(string token, CancellationToken cancellationToken = default)
    {
        using var doc = await CallAsync(token, "getMe", new { }, cancellationToken);
        var r = doc.RootElement.GetProperty("result");
        return new TelegramBotInfo(
            r.GetProperty("id").GetInt64(),
            r.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "",
            r.TryGetProperty("first_name", out var f) ? f.GetString() ?? "" : "");
    }

    /// <summary>Quita un webhook previo: con webhook activo Telegram no entrega getUpdates.</summary>
    public async Task DeleteWebhookAsync(string token, CancellationToken cancellationToken = default)
    {
        using var _ = await CallAsync(token, "deleteWebhook", new { drop_pending_updates = false }, cancellationToken);
    }

    public async Task<long> SendMessageAsync(
        string token,
        long chatId,
        string html,
        IReadOnlyList<IReadOnlyList<TelegramButton>>? buttons = null,
        long? replyToMessageId = null,
        bool forceReply = false,
        CancellationToken cancellationToken = default)
    {
        object? markup = forceReply
            ? new { force_reply = true, input_field_placeholder = "Escribe tu respuesta" }
            : BuildKeyboard(buttons);

        using var doc = await CallAsync(token, "sendMessage", new
        {
            chat_id = chatId,
            text = Truncate(html, 4000),
            parse_mode = "HTML",
            disable_web_page_preview = true,
            reply_markup = markup,
            reply_parameters = replyToMessageId.HasValue ? new { message_id = replyToMessageId.Value, allow_sending_without_reply = true } : null
        }, cancellationToken);
        return doc.RootElement.GetProperty("result").GetProperty("message_id").GetInt64();
    }

    public async Task EditMessageAsync(
        string token,
        long chatId,
        long messageId,
        string html,
        IReadOnlyList<IReadOnlyList<TelegramButton>>? buttons = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var _ = await CallAsync(token, "editMessageText", new
            {
                chat_id = chatId,
                message_id = messageId,
                text = Truncate(html, 4000),
                parse_mode = "HTML",
                disable_web_page_preview = true,
                reply_markup = BuildKeyboard(buttons) ?? new { inline_keyboard = Array.Empty<object>() }
            }, cancellationToken);
        }
        catch (TelegramApiException exception) when (exception.Message.Contains("not modified", StringComparison.OrdinalIgnoreCase))
        {
            // Mismo texto: no es un error.
        }
    }

    public async Task AnswerCallbackAsync(string token, string callbackId, string? text = null, CancellationToken cancellationToken = default)
    {
        try
        {
            using var _ = await CallAsync(token, "answerCallbackQuery", new { callback_query_id = callbackId, text }, cancellationToken);
        }
        catch (Exception exception) when (exception is TelegramApiException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // El botón ya expiró o falló la red: es solo el aviso del botón, la acción sigue.
        }
    }

    public async Task<IReadOnlyList<TelegramIncoming>> GetUpdatesAsync(string token, long offset, CancellationToken cancellationToken = default)
    {
        using var doc = await CallAsync(token, "getUpdates", new
        {
            offset,
            timeout = 0,
            limit = 50,
            allowed_updates = new[] { "message", "callback_query" }
        }, cancellationToken);

        var list = new List<TelegramIncoming>();
        foreach (var u in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var updateId = u.GetProperty("update_id").GetInt64();
            if (u.TryGetProperty("message", out var m))
            {
                var chat = m.GetProperty("chat");
                list.Add(new TelegramIncoming(
                    updateId,
                    chat.GetProperty("id").GetInt64(),
                    ChatName(chat),
                    m.GetProperty("message_id").GetInt64(),
                    m.TryGetProperty("text", out var t) ? t.GetString() : null,
                    m.TryGetProperty("reply_to_message", out var r) ? r.GetProperty("message_id").GetInt64() : null,
                    null, null, null));
            }
            else if (u.TryGetProperty("callback_query", out var c) && c.TryGetProperty("message", out var cm))
            {
                var chat = cm.GetProperty("chat");
                list.Add(new TelegramIncoming(
                    updateId,
                    chat.GetProperty("id").GetInt64(),
                    ChatName(chat),
                    null, null, null,
                    c.GetProperty("id").GetString(),
                    c.TryGetProperty("data", out var d) ? d.GetString() : null,
                    cm.GetProperty("message_id").GetInt64()));
            }
            else
            {
                list.Add(new TelegramIncoming(updateId, 0, null, null, null, null, null, null, null));
            }
        }
        return list;
    }

    // ─────────────────────────────────────────────────────────────

    /// <summary>Manda un archivo (PDF de etiquetas, por ejemplo) con texto y botones opcionales.</summary>
    public async Task<long> SendDocumentAsync(
        string token,
        long chatId,
        byte[] content,
        string fileName,
        string contentType,
        string? captionHtml = null,
        IReadOnlyList<IReadOnlyList<TelegramButton>>? buttons = null,
        CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "chat_id");
        if (!string.IsNullOrWhiteSpace(captionHtml))
        {
            form.Add(new StringContent(Truncate(captionHtml, 1000)), "caption");
            form.Add(new StringContent("HTML"), "parse_mode");
        }
        if (BuildKeyboard(buttons) is { } keyboard)
            form.Add(new StringContent(JsonSerializer.Serialize(keyboard, Json)), "reply_markup");
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(file, "document", fileName);

        using var response = await client.PostAsync($"{_apiBase}/bot{token.Trim()}/sendDocument", form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var description = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : null;
            throw new TelegramApiException(description ?? $"Telegram respondió HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
        }
        return doc.RootElement.GetProperty("result").GetProperty("message_id").GetInt64();
    }

    private async Task<JsonDocument> CallAsync(string token, string method, object payload, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var body = new StringContent(JsonSerializer.Serialize(payload, Json), System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"{_apiBase}/bot{token.Trim()}/{method}", body, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(content) ? "{}" : content);
        }
        catch (JsonException)
        {
            throw new TelegramApiException($"Telegram respondió HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
        }

        if (!doc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var description = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : null;
            doc.Dispose();
            throw new TelegramApiException(description ?? $"Telegram respondió HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
        }
        return doc;
    }

    private static object? BuildKeyboard(IReadOnlyList<IReadOnlyList<TelegramButton>>? buttons) =>
        buttons is null || buttons.Count == 0
            ? null
            : new
            {
                inline_keyboard = buttons.Select(row => row.Select(b => (object)(b.Url is not null
                    ? new { text = b.Text, url = b.Url }
                    : new { text = b.Text, callback_data = b.CallbackData }))).ToArray()
            };

    private static string? ChatName(JsonElement chat)
    {
        if (chat.TryGetProperty("username", out var u) && u.GetString() is { Length: > 0 } username)
            return "@" + username;
        var first = chat.TryGetProperty("first_name", out var f) ? f.GetString() : null;
        var last = chat.TryGetProperty("last_name", out var l) ? l.GetString() : null;
        var title = chat.TryGetProperty("title", out var t) ? t.GetString() : null;
        return title ?? string.Join(' ', new[] { first, last }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

public sealed class TelegramApiException : Exception
{
    public int StatusCode { get; }
    public TelegramApiException(string message, int statusCode) : base(message) => StatusCode = statusCode;

    /// <summary>El usuario bloqueó el bot o el token ya no sirve.</summary>
    public bool IsUnauthorizedOrBlocked => StatusCode is 401 or 403;
}
