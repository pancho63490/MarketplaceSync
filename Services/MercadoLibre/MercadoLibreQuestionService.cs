using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceSync.Services.Ai;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Questions;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.MercadoLibre;

public sealed record QuestionSyncResult(int Accounts, int New, int Unanswered, IReadOnlyList<string> Warnings);

public interface IMercadoLibreQuestionService
{
    /// <summary>Trae las preguntas sin responder de cada cuenta y prepara una respuesta con IA.</summary>
    Task<QuestionSyncResult> SyncOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default);

    Task<int> SyncAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Procesa una pregunta recibida por aviso de Mercado Libre (/questions/{id}).</summary>
    Task ProcessQuestionAsync(int marketplaceAccountId, string questionId, CancellationToken cancellationToken = default);

    /// <summary>Vuelve a pedir una respuesta a la IA.</summary>
    Task<string?> SuggestAsync(Guid organizationId, long questionId, CancellationToken cancellationToken = default);

    /// <summary>Envía la respuesta a Mercado Libre.</summary>
    Task AnswerAsync(Guid organizationId, long questionId, string text, CancellationToken cancellationToken = default);

    /// <summary>Quita datos de contacto que Mercado Libre prohíbe (teléfonos, correos, links).</summary>
    (string Text, bool Removed) Sanitize(string text);
}

public sealed class MercadoLibreQuestionService : IMercadoLibreQuestionService
{
    private const int MaxAnswerLength = 2000;
    private static readonly Regex EmailPattern = new(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.Compiled);
    private static readonly Regex UrlPattern = new(@"(https?://\S+|www\.\S+|\b[\w-]+\.(com|mx|net|org)(/\S*)?\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PhonePattern = new(@"(\+?\d[\d\s().-]{7,}\d)", RegexOptions.Compiled);
    private static readonly Regex ContactWords = new(@"\b(whats\s?app|wsp|telegram|facebook|instagram|ll[aá]mame|escr[ií]beme)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMercadoLibreAuthService _authService;
    private readonly ClaudeClient _claude;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MercadoLibreQuestionService> _logger;

    public MercadoLibreQuestionService(
        AppDbContext context,
        IHttpClientFactory httpClientFactory,
        IMercadoLibreAuthService authService,
        ClaudeClient claude,
        IConfiguration configuration,
        ILogger<MercadoLibreQuestionService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _authService = authService;
        _claude = claude;
        _configuration = configuration;
        _logger = logger;
    }

    private string ApiBase
    {
        get
        {
            var value = _configuration["MercadoLibre:ApiBaseUrl"];
            return string.IsNullOrWhiteSpace(value) ? "https://api.mercadolibre.com" : value.TrimEnd('/');
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Sincronizar
    // ─────────────────────────────────────────────────────────────

    public async Task<QuestionSyncResult> SyncOrganizationAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var created = 0;
        var accounts = await _context.MarketplaceAccounts
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Marketplace == "MercadoLibre" && x.Status == "Connected")
            .ToListAsync(cancellationToken);

        foreach (var account in accounts)
        {
            try
            {
                var token = await _authService.GetValidAccessTokenAsync(organizationId, account.Id);
                using var document = await GetJsonAsync(token,
                    $"{ApiBase}/my/received_questions/search?status=UNANSWERED&limit=50&sort_fields=date_created&sort_types=DESC",
                    cancellationToken);

                var remote = document.RootElement.TryGetProperty("questions", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(q => q.Clone()).ToList()
                    : new List<JsonElement>();
                var remoteIds = new HashSet<string>(remote.Select(q => ReadId(q, "id")).Where(x => x != null).Cast<string>());

                foreach (var question in remote)
                {
                    if (await UpsertAsync(organizationId, account.Id, question, cancellationToken))
                        created++;
                }

                // Las que estaban pendientes y ya no vienen: se respondieron en Mercado Libre o se cerraron.
                var stale = await _context.BuyerQuestions
                    .Where(q => q.MarketplaceAccountId == account.Id &&
                                q.Status == BuyerQuestionStatus.Unanswered &&
                                !remoteIds.Contains(q.ExternalQuestionId))
                    .Take(20)
                    .ToListAsync(cancellationToken);
                foreach (var question in stale)
                {
                    try
                    {
                        using var detail = await GetJsonAsync(token, $"{ApiBase}/questions/{question.ExternalQuestionId}", cancellationToken);
                        Apply(question, detail.RootElement);
                    }
                    catch (HttpRequestException)
                    {
                        question.Status = "DELETED";
                    }
                }

                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Question sync failed for account {AccountId}.", account.Id);
                warnings.Add($"No se pudieron leer las preguntas de {account.DisplayName ?? account.ExternalAccountId}.");
            }
        }

        // Sugerencias de IA para las nuevas (máximo 10 por vuelta para no tardar).
        if (_claude.IsConfigured)
        {
            var pending = await _context.BuyerQuestions
                .Where(q => q.OrganizationId == organizationId &&
                            q.Status == BuyerQuestionStatus.Unanswered &&
                            q.SuggestedAnswer == null)
                .OrderByDescending(q => q.DateCreated)
                .Take(10)
                .Select(q => q.Id)
                .ToListAsync(cancellationToken);
            foreach (var id in pending)
                await SuggestAsync(organizationId, id, cancellationToken);
        }

        var unanswered = await _context.BuyerQuestions.CountAsync(
            q => q.OrganizationId == organizationId && q.Status == BuyerQuestionStatus.Unanswered, cancellationToken);
        return new QuestionSyncResult(accounts.Count, created, unanswered, warnings);
    }

    public async Task<int> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        var organizationIds = await _context.MarketplaceAccounts
            .AsNoTracking()
            .Where(x => x.Marketplace == "MercadoLibre" && x.Status == "Connected")
            .Select(x => x.OrganizationId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var total = 0;
        foreach (var organizationId in organizationIds)
        {
            try
            {
                total += (await SyncOrganizationAsync(organizationId, cancellationToken)).New;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(exception, "Question sync failed for organization {OrganizationId}.", organizationId);
            }
            finally
            {
                _context.ChangeTracker.Clear();
            }
        }

        return total;
    }

    public async Task ProcessQuestionAsync(int marketplaceAccountId, string questionId, CancellationToken cancellationToken = default)
    {
        var account = await _context.MarketplaceAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == marketplaceAccountId, cancellationToken);
        if (account is null || !Regex.IsMatch(questionId, @"^\d{1,20}$"))
            return;

        var token = await _authService.GetValidAccessTokenAsync(account.OrganizationId, account.Id);
        using var document = await GetJsonAsync(token, $"{ApiBase}/questions/{questionId}", cancellationToken);
        await UpsertAsync(account.OrganizationId, account.Id, document.RootElement, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        var question = await _context.BuyerQuestions.AsNoTracking()
            .FirstOrDefaultAsync(q => q.MarketplaceAccountId == account.Id && q.ExternalQuestionId == questionId, cancellationToken);
        if (question is { Status: BuyerQuestionStatus.Unanswered, SuggestedAnswer: null } && _claude.IsConfigured)
            await SuggestAsync(account.OrganizationId, question.Id, cancellationToken);
    }

    private async Task<bool> UpsertAsync(Guid organizationId, int accountId, JsonElement json, CancellationToken cancellationToken)
    {
        var externalId = ReadId(json, "id");
        if (string.IsNullOrWhiteSpace(externalId))
            return false;

        var question = await _context.BuyerQuestions
            .FirstOrDefaultAsync(q => q.MarketplaceAccountId == accountId && q.ExternalQuestionId == externalId, cancellationToken);
        var created = question is null;
        if (question is null)
        {
            question = new BuyerQuestion
            {
                OrganizationId = organizationId,
                MarketplaceAccountId = accountId,
                ExternalQuestionId = externalId
            };
            _context.BuyerQuestions.Add(question);
        }

        Apply(question, json);

        if (question.ProductId is null && !string.IsNullOrWhiteSpace(question.ItemId))
        {
            question.ProductId = await _context.MarketplacePublications
                .Where(p => p.ExternalItemId == question.ItemId && p.Product.OrganizationId == organizationId)
                .Select(p => (int?)p.ProductId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return created;
    }

    private static void Apply(BuyerQuestion question, JsonElement json)
    {
        question.Text = Truncate(ReadString(json, "text") ?? question.Text, 2000)!;
        question.ItemId = ReadString(json, "item_id") ?? question.ItemId;
        question.Status = ReadString(json, "status") ?? question.Status;
        if (DateTime.TryParse(ReadString(json, "date_created"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created))
            question.DateCreated = created;
        if (json.TryGetProperty("answer", out var answer) && answer.ValueKind == JsonValueKind.Object)
        {
            question.AnswerText = Truncate(ReadString(answer, "text"), 2000) ?? question.AnswerText;
            if (DateTime.TryParse(ReadString(answer, "date_created"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var answered))
                question.AnsweredAt = answered;
        }
        question.SyncedAt = DateTime.UtcNow;
    }

    // ─────────────────────────────────────────────────────────────
    // IA
    // ─────────────────────────────────────────────────────────────

    public async Task<string?> SuggestAsync(Guid organizationId, long questionId, CancellationToken cancellationToken = default)
    {
        var question = await _context.BuyerQuestions
            .Include(q => q.Product).ThenInclude(p => p!.Variants)
            .Include(q => q.Product).ThenInclude(p => p!.MarketplacePublications)
            .FirstOrDefaultAsync(q => q.Id == questionId && q.OrganizationId == organizationId, cancellationToken);
        if (question is null || !_claude.IsConfigured)
            return null;

        var product = question.Product;
        var publication = product?.MarketplacePublications.FirstOrDefault(p => p.ExternalItemId == question.ItemId);
        var stock = product?.Variants.Where(v => v.IsActive).Sum(v => Math.Max(0, v.Stock)) is int own and > 0
            ? own
            : publication?.Stock;

        var previous = await _context.BuyerQuestions
            .AsNoTracking()
            .Where(q => q.OrganizationId == organizationId && q.ItemId == question.ItemId &&
                        q.Id != question.Id && q.AnswerText != null)
            .OrderByDescending(q => q.AnsweredAt)
            .Take(5)
            .Select(q => new { q.Text, q.AnswerText })
            .ToListAsync(cancellationToken);

        var facts = new StringBuilder();
        facts.AppendLine($"Publicación: {question.ItemId}");
        if (product is not null)
        {
            facts.AppendLine($"Título: {product.Title}");
            if (!string.IsNullOrWhiteSpace(product.Brand)) facts.AppendLine($"Marca: {product.Brand}");
            if (!string.IsNullOrWhiteSpace(product.Model)) facts.AppendLine($"Modelo / número de parte: {product.Model}");
            if (!string.IsNullOrWhiteSpace(product.Description)) facts.AppendLine($"Descripción:\n{Truncate(product.Description, 2500)}");
        }
        if (publication?.Price is > 0) facts.AppendLine($"Precio: {publication.Price.Value.ToString("C0", CultureInfo.GetCultureInfo("es-MX"))}");
        if (stock.HasValue) facts.AppendLine($"Piezas disponibles: {stock}");
        if (publication?.Condition is not null) facts.AppendLine($"Condición: {(publication.Condition == "used" ? "usado" : "nuevo")}");
        foreach (var attribute in ListingAutopilotService.ReadSavedAttributes(publication?.AttributesJson))
            facts.AppendLine($"{attribute.Id}: {attribute.ValueName}");
        foreach (var item in previous)
            facts.AppendLine($"Pregunta anterior: {item.Text} → Respuesta: {item.AnswerText}");

        var system = """
            Eres quien atiende las preguntas de una tienda de autopartes y productos importados en Mercado Libre México.
            Responde la pregunta del comprador en español de México: amable, directo y en 1 a 3 frases (máximo 350 caracteres).
            Reglas obligatorias de Mercado Libre: no incluyas teléfonos, correos, links, redes sociales ni invites a contactar fuera de Mercado Libre.
            Usa solo los datos dados. Si la respuesta depende de un dato que no está (por ejemplo compatibilidad con un vehículo no mencionado),
            pide al comprador el dato que falta (año, motor, versión o VIN) en lugar de inventar.
            No prometas tiempos de entrega exactos: di que el envío es por Mercado Envíos y el tiempo aparece en la publicación.
            Empieza con "Hola" y termina con "¡Saludos!". Responde solo con el texto de la respuesta.
            """;
        var suggestion = await _claude.CompleteAsync(
            system,
            $"DATOS DEL PRODUCTO:\n{facts}\nPREGUNTA DEL COMPRADOR:\n{question.Text}",
            400,
            0.3,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(suggestion))
            return null;

        var clean = RemoveContactSentences(suggestion);
        question.SuggestedAnswer = Truncate(clean, MaxAnswerLength);
        question.SuggestedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        return question.SuggestedAnswer;
    }

    // ─────────────────────────────────────────────────────────────
    // Responder
    // ─────────────────────────────────────────────────────────────

    public async Task AnswerAsync(Guid organizationId, long questionId, string text, CancellationToken cancellationToken = default)
    {
        var question = await _context.BuyerQuestions
            .FirstOrDefaultAsync(q => q.Id == questionId && q.OrganizationId == organizationId, cancellationToken)
            ?? throw new KeyNotFoundException("No se encontró la pregunta.");
        if (question.Status != BuyerQuestionStatus.Unanswered)
            throw new InvalidOperationException("Esta pregunta ya no está pendiente en Mercado Libre.");

        var (clean, removed) = Sanitize(text ?? string.Empty);
        if (clean.Length < 2)
            throw new ArgumentException("Escribe la respuesta.");
        if (removed)
            throw new ArgumentException("Mercado Libre no permite teléfonos, correos, links ni redes sociales en las respuestas. Quítalos y vuelve a enviar.");
        if (clean.Length > MaxAnswerLength)
            throw new ArgumentException("La respuesta es demasiado larga (máximo 2,000 caracteres).");

        var token = await _authService.GetValidAccessTokenAsync(organizationId, question.MarketplaceAccountId);
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/answers");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { question_id = long.Parse(question.ExternalQuestionId, CultureInfo.InvariantCulture), text = clean }),
            Encoding.UTF8,
            "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var message = MercadoLibreListingClient.DescribeError(content);
            question.LastError = Truncate(message, 500);
            await _context.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("Mercado Libre rechazó la respuesta: " + message);
        }

        question.Status = BuyerQuestionStatus.Answered;
        question.AnswerText = clean;
        question.AnsweredAt = DateTime.UtcNow;
        question.LastError = null;
        await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Para lo que escribe la IA: quita completas las frases con datos de contacto (no deja frases a medias).</summary>
    private string RemoveContactSentences(string text)
    {
        var sentences = Regex.Split(text.Trim(), @"(?<=[.!?])\s+");
        var kept = sentences.Where(sentence => !Sanitize(sentence).Removed);
        var result = string.Join(" ", kept).Trim();
        return result.Length > 0 ? result : Sanitize(text).Text;
    }

    public (string Text, bool Removed) Sanitize(string text)
    {
        var original = text.Trim();
        var clean = EmailPattern.Replace(original, string.Empty);
        clean = UrlPattern.Replace(clean, string.Empty);
        clean = PhonePattern.Replace(clean, m => LooksLikePhone(m.Value) ? string.Empty : m.Value);
        clean = ContactWords.Replace(clean, string.Empty);
        clean = Regex.Replace(clean, @"[ \t]{2,}", " ").Trim();
        return (clean, !string.Equals(clean, Regex.Replace(original, @"[ \t]{2,}", " ").Trim(), StringComparison.Ordinal));
    }

    /// <summary>Un teléfono tiene 10 dígitos o más; años (2014-2018), motores (5.3) y números de parte cortos no cuentan.</summary>
    private static bool LooksLikePhone(string value)
    {
        var groups = Regex.Matches(value, @"\d+").Select(g => g.Value).ToList();
        if (groups.Sum(g => g.Length) < 10)
            return false;
        return !groups.All(g => g.Length <= 2 || Regex.IsMatch(g, @"^(19|20)\d{2}$"));
    }

    // ─────────────────────────────────────────────────────────────
    // Utilidades
    // ─────────────────────────────────────────────────────────────

    private async Task<JsonDocument> GetJsonAsync(string token, string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Mercado Libre respondió HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        return JsonDocument.Parse(content);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ReadId(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.String => value.GetString(),
                _ => null
            }
            : null;

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
