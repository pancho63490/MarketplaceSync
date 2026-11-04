using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MarketplaceSync.Services.Ai;

/// <summary>
/// Cliente mínimo para la API de Mensajes de Claude (Anthropic).
/// Configuración:
///   Anthropic:ApiKey  (o variable de entorno ANTHROPIC_API_KEY)
///   Anthropic:Model   (predeterminado claude-haiku-5-5: rápido y económico para títulos y traducciones)
///   Anthropic:Version (encabezado anthropic-version, predeterminado 2023-06-01)
/// Nunca lanza excepciones de red al llamador: devuelve null para que cada servicio use su respaldo por reglas.
/// </summary>
public sealed class ClaudeClient
{
    public const string HttpClientName = "Anthropic";
    private const string DefaultModel = "claude-haiku-5-5";
    private const string DefaultApiVersion = "2023-06-01";
    private const string Endpoint = "https://api.anthropic.com/v1/messages";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClaudeClient> _logger;

    public ClaudeClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ClaudeClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public string Model
    {
        get
        {
            var model = _configuration["Anthropic:Model"];
            return string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        }
    }

    private string? ApiKey
    {
        get
        {
            var key = _configuration["Anthropic:ApiKey"];
            return string.IsNullOrWhiteSpace(key)
                ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                : key;
        }
    }

    /// <summary>Envía un mensaje y devuelve el texto de la respuesta, o null si falla.</summary>
    public Task<string?> CompleteAsync(
        string system,
        string userMessage,
        int maxTokens,
        double temperature = 0.2,
        CancellationToken cancellationToken = default) =>
        ChatAsync(system, new[] { new ChatTurn("user", userMessage) }, maxTokens, temperature, null, cancellationToken);

    /// <summary>
    /// Conversación de varios turnos (user/assistant alternados, empezando por user).
    /// Devuelve el texto de la respuesta o null si falla.
    /// </summary>
    public async Task<string?> ChatAsync(
        string system,
        IReadOnlyList<ChatTurn> turns,
        int maxTokens,
        double temperature = 0.3,
        string? modelOverride = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey) || turns.Count == 0)
            return null;

        var version = _configuration["Anthropic:Version"];
        if (string.IsNullOrWhiteSpace(version))
            version = DefaultApiVersion;

        var messages = new JsonArray();
        foreach (var turn in turns)
        {
            messages.Add(new JsonObject
            {
                ["role"] = turn.Role == "assistant" ? "assistant" : "user",
                ["content"] = turn.Content
            });
        }

        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(modelOverride) ? Model : modelOverride,
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
            ["system"] = system,
            ["messages"] = messages
        };

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            // Anthropic:BaseUrl solo se usa para pruebas locales; en producción queda vacío.
            var baseUrl = _configuration["Anthropic:BaseUrl"];
            var endpoint = string.IsNullOrWhiteSpace(baseUrl) ? Endpoint : baseUrl.TrimEnd('/') + "/v1/messages";
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", version);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Claude API returned HTTP {StatusCode}: {Body}",
                    (int)response.StatusCode,
                    body.Length > 800 ? body[..800] : body);
                return null;
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
                return null;

            var text = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) &&
                    type.GetString() == "text" &&
                    block.TryGetProperty("text", out var value))
                {
                    text.Append(value.GetString());
                }
            }

            return text.Length == 0 ? null : text.ToString().Trim();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Claude API call failed.");
            return null;
        }
    }

    /// <summary>
    /// Pide una respuesta JSON y la deserializa. Tolera texto alrededor del objeto o bloques ```json.
    /// </summary>
    public async Task<T?> CompleteJsonAsync<T>(
        string system,
        string userMessage,
        int maxTokens,
        CancellationToken cancellationToken = default) where T : class
    {
        var text = await CompleteAsync(
            system + "\nResponde únicamente con un objeto JSON válido, sin texto adicional ni bloques de código.",
            userMessage,
            maxTokens,
            0.1,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(text))
            return null;

        var json = ExtractJsonObject(text);
        if (json is null)
        {
            _logger.LogWarning("Claude response did not contain a JSON object.");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Claude JSON response could not be parsed.");
            return null;
        }
    }

    private static string? ExtractJsonObject(string text)
    {
        var fenced = Regex.Match(text, @"```(?:json)?\s*(\{[\s\S]*\})\s*```", RegexOptions.IgnoreCase);
        if (fenced.Success)
            return fenced.Groups[1].Value;

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }
}

public sealed record ChatTurn(string Role, string Content);
