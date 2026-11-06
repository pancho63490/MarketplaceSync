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
    /// Prueba la clave con una llamada mínima y devuelve un mensaje claro para el vendedor
    /// (clave inválida, sin saldo, modelo no disponible, sin conexión).
    /// </summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, "No hay clave: agrega Anthropic__ApiKey en Render (Environment) y vuelve a desplegar.");

        var version = _configuration["Anthropic:Version"];
        if (string.IsNullOrWhiteSpace(version))
            version = DefaultApiVersion;
        var agentModel = _configuration["Anthropic:AgentModel"];
        var models = new[] { Model, string.IsNullOrWhiteSpace(agentModel) ? "claude-sonnet-5-5" : agentModel.Trim() }.Distinct().ToList();

        foreach (var model in models)
        {
            var payload = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 5,
                ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Responde: ok" } }
            };
            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                var baseUrl = _configuration["Anthropic:BaseUrl"];
                var endpoint = string.IsNullOrWhiteSpace(baseUrl) ? Endpoint : baseUrl.TrimEnd('/') + "/v1/messages";
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Add("x-api-key", apiKey);
                request.Headers.Add("anthropic-version", version);
                request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                    continue;

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                string? type = null, message = null;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("error", out var error))
                    {
                        type = error.TryGetProperty("type", out var t) ? t.GetString() : null;
                        message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                    }
                }
                catch (JsonException) { }

                var status = (int)response.StatusCode;
                if (status == 401 || type == "authentication_error")
                    return (false, "La clave no es válida. Crea una en platform.claude.com → Settings → API Keys (empieza con sk-ant-api) y ponla en Render.");
                if (message is not null && message.Contains("credit", StringComparison.OrdinalIgnoreCase))
                    return (false, "La clave sirve, pero tu cuenta no tiene saldo. Carga créditos en la Consola (Billing).");
                if (status == 403 || type == "permission_error")
                    return (false, "La clave no tiene permiso para usar la API. Revisa la organización de la clave en la Consola.");
                if (status == 404 || type == "not_found_error")
                    return (false, $"La clave sirve, pero el modelo {model} no está disponible para tu cuenta. Cambia Anthropic__Model o Anthropic__AgentModel en Render.");
                if (status == 429)
                    return (false, "La clave sirve, pero llegaste al límite de uso. Revisa los límites en la Consola o espera unos minutos.");
                return (false, $"Claude respondió con un error ({status}). Intenta de nuevo en unos minutos.");
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Claude test failed.");
                return (false, "No se pudo conectar con Claude desde el servidor. Intenta de nuevo en unos minutos.");
            }
        }

        return (true, $"Funciona: la IA respondió con {string.Join(" y ", models)}. El agente, las preguntas y los títulos ya usan IA.");
    }

    /// <summary>
    /// Llamada con herramientas (tool use) para agentes: envía los mensajes tal cual
    /// (pueden traer bloques tool_use / tool_result) y devuelve la respuesta completa, o null si falla.
    /// El modelo sale de Anthropic:AgentModel (predeterminado claude-sonnet-5-5: mejor criterio que Haiku para decidir).
    /// </summary>
    public async Task<JsonDocument?> SendWithToolsAsync(
        string system,
        JsonArray messages,
        JsonArray tools,
        int maxTokens,
        CancellationToken cancellationToken = default)
    {
        var apiKey = ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var version = _configuration["Anthropic:Version"];
        if (string.IsNullOrWhiteSpace(version))
            version = DefaultApiVersion;
        var model = _configuration["Anthropic:AgentModel"];

        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-5-5" : model.Trim(),
            ["max_tokens"] = maxTokens,
            ["system"] = system,
            ["tools"] = JsonNode.Parse(tools.ToJsonString()),
            ["messages"] = JsonNode.Parse(messages.ToJsonString())
        };

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
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
                _logger.LogWarning("Claude tools call returned HTTP {StatusCode}: {Body}",
                    (int)response.StatusCode, body.Length > 800 ? body[..800] : body);
                return null;
            }
            return JsonDocument.Parse(body);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Claude tools call failed.");
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
