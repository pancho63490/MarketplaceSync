using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceSync.Services.Ai;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.Notifications;
using MarketplaceSync.Services.Purchasing;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Agents;
using MarketplaceSync.Web.Models.Purchasing;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Agents;

/// <summary>
/// Agente de importación: cada día revisa con Claude (tool use) los candidatos del Radar como lo haría el vendedor
/// —que la oferta de la tienda sea la misma pieza que se vende en Mercado Libre, que no la tenga ya, que el margen aguante—
/// importa y prepara con IA los que valen la pena (sin publicar), descarta los demás con su razón
/// y manda el reporte a Telegram. Sin IA configurada usa la regla anterior (los de más demanda y ganancia).
/// </summary>
public sealed class ImportAgentService
{
    private const int MaxTurns = 30;
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    private readonly AppDbContext _context;
    private readonly ClaudeClient _claude;
    private readonly IPurchaseRadarService _radar;
    private readonly IListingAutopilotService _autopilot;
    private readonly ICrossBorderPricingService _pricing;
    private readonly RadarAutoPrepareService _rules;
    private readonly TelegramNotificationService _telegram;
    private readonly ILogger<ImportAgentService> _logger;
    private readonly MarketplaceSync.Web.Services.EbayApiService _ebay;
    private readonly MarketplaceSync.Web.Services.ProductExtractorService _extractor;
    private readonly ProductTitleTranslator _titleTranslator;
    private readonly IMercadoLibreAuthService _mlAuth;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly MarketplaceSync.Services.MarketInsights.CompetitorService _competitors;

    public ImportAgentService(
        AppDbContext context,
        ClaudeClient claude,
        IPurchaseRadarService radar,
        IListingAutopilotService autopilot,
        ICrossBorderPricingService pricing,
        RadarAutoPrepareService rules,
        TelegramNotificationService telegram,
        ILogger<ImportAgentService> logger,
        MarketplaceSync.Web.Services.EbayApiService ebay,
        MarketplaceSync.Web.Services.ProductExtractorService extractor,
        ProductTitleTranslator titleTranslator,
        IMercadoLibreAuthService mlAuth,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        MarketplaceSync.Services.MarketInsights.CompetitorService competitors)
    {
        _competitors = competitors;
        _ebay = ebay;
        _extractor = extractor;
        _titleTranslator = titleTranslator;
        _mlAuth = mlAuth;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _context = context;
        _claude = claude;
        _radar = radar;
        _autopilot = autopilot;
        _pricing = pricing;
        _rules = rules;
        _telegram = telegram;
        _logger = logger;
    }

    private const string SystemPrompt = """
        Eres el agente de importación de una tienda de Mercado Libre México. El vendedor compra en El Paso (Amazon, eBay, Walmart,
        RockAuto) y cruza a Ciudad Juárez; vende sobre todo refacciones de auto y también dulces y bebidas americanas.
        Tu trabajo: decidir qué productos del Radar vale la pena importar al catálogo (quedan preparados con IA, sin publicar;
        se publican solos cuando el vendedor recibe la mercancía).

        Cómo trabajar:
        1. Llama a ver_reglas para saber cuántos puedes importar hoy y la ganancia mínima.
        2. Llama a ver_candidatos y revisa a fondo (revisar_candidato) los más prometedores antes de decidir.
        3. Importa solo si TODO esto se cumple:
           - La oferta de la tienda es la MISMA pieza que se vende en Mercado Libre: mismo número de parte o modelo, misma marca
             (o equivalente explícito), misma cantidad/presentación (un par no es una pieza; 12 latas no es 24) y condición nueva.
           - Si es refacción, la compatibilidad (marca, modelo, años, motor, lado) no se contradice entre ambos títulos.
           - No está ya en el catálogo (revisar_candidato te dice si hay uno parecido).
           - La ganancia estimada por pieza es al menos la mínima y el margen es razonable; prefiere demanda alta y tendencia al alza.
           - No es réplica, genérico con marca famosa, producto prohibido o peligroso para cruzar (baterías de litio sueltas,
             químicos inflamables, armas, medicamentos).
        4. Descarta con descartar (y una razón corta y concreta) los que claramente no convienen o no coinciden. Si solo tienes
           una duda menor, NO lo descartes: déjalo sin tocar para que el vendedor lo vea.
        5. No pases del límite de importaciones. Cuando termines, llama a terminar con un resumen de 2 o 3 frases para el vendedor.

        Escribe las razones en español de México, cortas y en lenguaje de negocio (ej. "La oferta de eBay es el par; en ML se vende la pieza sola").
        """;

    private const string RequestPrompt = """

        ENCARGO DEL VENDEDOR: además del Radar, el vendedor te pidió buscar algo concreto. Para eso:
        1. Busca en Mercado Libre (buscar_en_mercado_libre) qué se vende de eso y a qué precio: es la demanda.
           Elige productos concretos que se venden bien (con marca y número de parte o presentación clara).
        2. Para cada uno, busca la MISMA pieza en eBay (buscar_en_ebay) con marca + número de parte o modelo exacto.
        3. Calcula la ganancia (calcular_ganancia) con el costo + envío de eBay y el precio al que se vende en Mercado Libre.
        4. Si coincide y deja al menos la ganancia mínima, impórtalo con importar_link (link de eBay, costo y precio de referencia).
        Si el encargo es copiar a una tienda (trae su vendedor_id), usa ver_productos_de_tienda y empieza por lo que más vende;
        busca la misma pieza en eBay y solo importa lo que deje ganancia con el precio al que ellos lo venden.
        No importes genéricos sin marca, réplicas, ni cosas que no se pueden cruzar. Si no encuentras nada que convenga, dilo claro
        en el resumen y sugiere qué buscar en su lugar.
        """;

    private static readonly JsonArray RequestTools = JsonNode.Parse("""
        [
          {"name":"buscar_en_mercado_libre","description":"Busca en Mercado Libre México: títulos, precios y vendidos de lo que se vende para esa búsqueda, y la categoría.","input_schema":{"type":"object","properties":{"busqueda":{"type":"string"}},"required":["busqueda"]}},
          {"name":"ver_productos_de_tienda","description":"Productos de una tienda de la competencia en Mercado Libre (ordenados por vendidos): título, precio, vendidos. Úsalo cuando el encargo pida copiar a una tienda.","input_schema":{"type":"object","properties":{"vendedor_id":{"type":"string"},"busqueda":{"type":"string","description":"Opcional: filtrar sus productos."}},"required":["vendedor_id"]}},
          {"name":"buscar_en_ebay","description":"Busca en eBay US artículos nuevos de precio fijo enviados a El Paso: título, precio, envío, marca, condición y link.","input_schema":{"type":"object","properties":{"busqueda":{"type":"string","description":"En inglés, con marca y número de parte o modelo."},"limite":{"type":"integer"}},"required":["busqueda"]}},
          {"name":"calcular_ganancia","description":"Ganancia por pieza en Mercado Libre con tus costos de cruce: costo en dólares (más envío) contra el precio de venta en pesos.","input_schema":{"type":"object","properties":{"costo_usd":{"type":"number"},"envio_usd":{"type":"number"},"precio_ml_mxn":{"type":"number"}},"required":["costo_usd","precio_ml_mxn"]}},
          {"name":"importar_link","description":"Importa al catálogo el producto de ese link (eBay u otra tienda) y lo prepara con IA, sin publicar.","input_schema":{"type":"object","properties":{"link":{"type":"string"},"costo_usd":{"type":"number"},"precio_ml_mxn":{"type":"number","description":"Precio al que se vende en Mercado Libre (referencia)."},"titulo_ml":{"type":"string","description":"Cómo se llama en Mercado Libre (referencia para no repetir)."},"motivo":{"type":"string"}},"required":["link","motivo"]}}
        ]
        """)!.AsArray();

    private static readonly JsonArray Tools = JsonNode.Parse("""
        [
          {"name":"ver_reglas","description":"Cuántos productos puedes importar hoy, ganancia mínima por pieza, margen objetivo y tipo de cambio.","input_schema":{"type":"object","properties":{}}},
          {"name":"ver_candidatos","description":"Candidatos del Radar sin importar: demanda en Mercado Libre, oferta encontrada en la tienda y ganancia estimada. Ordenados por demanda y ganancia.","input_schema":{"type":"object","properties":{"limite":{"type":"integer","description":"Máximo a devolver (1-30). Predeterminado 20."}}}},
          {"name":"revisar_candidato","description":"Detalle de un candidato: título y datos en Mercado Libre vs. título y condición de la oferta en la tienda, costos, y productos parecidos que ya están en el catálogo.","input_schema":{"type":"object","properties":{"id":{"type":"integer"}},"required":["id"]}},
          {"name":"importar","description":"Importa el candidato al catálogo y lo prepara con IA (título, descripción, categoría, atributos, precio). No publica.","input_schema":{"type":"object","properties":{"id":{"type":"integer"},"motivo":{"type":"string","description":"Por qué conviene, en una frase."}},"required":["id","motivo"]}},
          {"name":"descartar","description":"Descarta el candidato para que no vuelva a sugerirse.","input_schema":{"type":"object","properties":{"id":{"type":"integer"},"motivo":{"type":"string","description":"Razón corta y concreta."}},"required":["id","motivo"]}},
          {"name":"terminar","description":"Termina la corrida con un resumen para el vendedor.","input_schema":{"type":"object","properties":{"resumen":{"type":"string"}},"required":["resumen"]}}
        ]
        """)!.AsArray();

    public async Task<IReadOnlyList<Guid>> GetOrganizationsAsync(CancellationToken cancellationToken = default) =>
        await _context.PurchaseCandidates.AsNoTracking()
            .Where(x => x.Status == PurchaseCandidateStatus.Suggested && x.ProductId == null)
            .Select(x => x.OrganizationId).Distinct().ToListAsync(cancellationToken);

    /// <summary>Corre el agente para una organización y devuelve la corrida guardada.</summary>
    public Task<AgentRun> RunAsync(Guid organizationId, string trigger, CancellationToken cancellationToken = default) =>
        RunAsync(organizationId, trigger, null, cancellationToken);

    /// <summary>Máximo de productos que importa un encargo (aparte del límite semanal del Radar).</summary>
    public const int MaxPerRequest = 10;

    public async Task<AgentRun> RunAsync(Guid organizationId, string trigger, string? request, CancellationToken cancellationToken = default)
    {
        var settings = await _pricing.GetOrCreateSettingsAsync(organizationId, cancellationToken);
        request = string.IsNullOrWhiteSpace(request) ? null : request.Trim();
        if (request?.Length > 500) request = request[..500];
        var run = new AgentRun { OrganizationId = organizationId, Trigger = trigger, Request = request, Engine = _claude.IsConfigured ? "ai" : "rules" };
        _context.AgentRuns.Add(run);
        await _context.SaveChangesAsync(cancellationToken);

        try
        {
            if (request is not null)
            {
                if (!_claude.IsConfigured)
                {
                    await FinishAsync(run, AgentRunStatus.Skipped, "Los encargos necesitan IA: agrega Anthropic__ApiKey en Render.", cancellationToken);
                    return run;
                }
                await RunAgentLoopAsync(run, settings, MaxPerRequest, cancellationToken);
                await FinishAsync(run, AgentRunStatus.Done, run.Summary, cancellationToken);
                await ReportAsync(run, cancellationToken);
                return run;
            }

            var budget = await RemainingBudgetAsync(organizationId, settings.AutoPrepareWeekly, cancellationToken);
            if (!settings.ImportAgentEnabled && trigger == "schedule")
            {
                await FinishAsync(run, AgentRunStatus.Skipped, "El agente está apagado en su configuración.", cancellationToken);
                return run;
            }
            if (budget <= 0)
            {
                await FinishAsync(run, AgentRunStatus.Skipped,
                    $"Ya se importaron los {settings.AutoPrepareWeekly} productos de esta semana. Sube el límite si quieres más.", cancellationToken);
                return run;
            }

            if (!_claude.IsConfigured)
            {
                var result = await _rules.PrepareTopAsync(organizationId, cancellationToken);
                run.Imported = result.Prepared;
                await FinishAsync(run, AgentRunStatus.Done,
                    $"Sin IA configurada: se prepararon {result.Prepared} con la regla de más demanda y ganancia. " +
                    "Agrega Anthropic__ApiKey en Render para que el agente revise cada producto.", cancellationToken);
                return run;
            }

            await RunAgentLoopAsync(run, settings, budget, cancellationToken);
            await FinishAsync(run, AgentRunStatus.Done, run.Summary, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Import agent failed for {OrganizationId}.", organizationId);
            run.Error = exception.Message.Length > 900 ? exception.Message[..900] : exception.Message;
            await FinishAsync(run, AgentRunStatus.Failed, run.Summary ?? "El agente se detuvo por un error; lo que ya importó se queda.", cancellationToken);
        }

        await ReportAsync(run, cancellationToken);
        return run;
    }

    private async Task<int> RemainingBudgetAsync(Guid organizationId, int weekly, CancellationToken cancellationToken)
    {
        var weekAgo = DateTime.UtcNow.AddDays(-7);
        var preparedThisWeek = await _context.PurchaseCandidates.CountAsync(
            x => x.OrganizationId == organizationId && x.PreparedAt >= weekAgo, cancellationToken);
        return Math.Clamp(weekly, 0, 50) - preparedThisWeek;
    }

    private async Task RunAgentLoopAsync(AgentRun run, Web.Models.CrossBorder.CrossBorderSettings settings, int budget, CancellationToken cancellationToken)
    {
        var org = run.OrganizationId;
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = run.Request is null
                    ? $"Hoy es {DateTime.UtcNow.AddHours(-7):dd/MM/yyyy}. Revisa el Radar e importa lo que valga la pena (máximo {budget})."
                    : $"Hoy es {DateTime.UtcNow.AddHours(-7):dd/MM/yyyy}. Encargo del vendedor: «{run.Request}». Busca en Mercado Libre y en eBay e importa lo que valga la pena (máximo {budget})."
            }
        };
        var system = run.Request is null ? SystemPrompt : SystemPrompt + RequestPrompt;
        var tools = run.Request is null ? Tools : new JsonArray(Tools.Concat(RequestTools).Select(t => JsonNode.Parse(t!.ToJsonString())).ToArray());
        var imported = 0;
        var reviewed = new HashSet<long>();

        for (var turn = 0; turn < MaxTurns; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await _claude.SendWithToolsAsync(system, messages, tools, 2000, cancellationToken)
                                 ?? throw new InvalidOperationException("La IA no respondió (puede ser falta de saldo o una falla momentánea). Pulsa “Probar la IA”; mañana lo vuelve a intentar solo.");
            var root = response.RootElement;
            if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Respuesta de Claude sin contenido.");

            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = JsonNode.Parse(content.GetRawText()) });

            var results = new JsonArray();
            var finished = false;
            foreach (var block in content.EnumerateArray())
            {
                if (block.GetProperty("type").GetString() != "tool_use")
                    continue;
                var id = block.GetProperty("id").GetString()!;
                var name = block.GetProperty("name").GetString()!;
                var input = block.TryGetProperty("input", out var i) ? i : default;

                string output;
                try
                {
                    switch (name)
                    {
                        case "ver_reglas":
                            output = JsonSerializer.Serialize(new
                            {
                                puedes_importar_hoy = budget - imported,
                                ganancia_minima_mxn = settings.MinProfitAmountMxn,
                                margen_objetivo_pct = settings.TargetProfitMarginPercent,
                                tipo_de_cambio = settings.UsdToMxnRate
                            });
                            break;
                        case "ver_candidatos":
                            output = await ListCandidatesAsync(org, Int(input, "limite") ?? 20, cancellationToken);
                            break;
                        case "revisar_candidato":
                            var reviewId = Long(input, "id");
                            if (reviewId.HasValue) reviewed.Add(reviewId.Value);
                            output = await ReviewCandidateAsync(org, reviewId, cancellationToken);
                            break;
                        case "importar":
                            if (imported >= budget)
                            {
                                output = "No: ya llegaste al límite de hoy. Llama a terminar.";
                                break;
                            }
                            var (ok, text) = await ImportAsync(run, Long(input, "id"), Str(input, "motivo"), cancellationToken);
                            if (ok) imported++;
                            output = text;
                            break;
                        case "descartar":
                            output = await DiscardAsync(run, Long(input, "id"), Str(input, "motivo"), cancellationToken);
                            break;
                        case "buscar_en_mercado_libre":
                            output = await SearchMercadoLibreAsync(org, Str(input, "busqueda"), cancellationToken);
                            break;
                        case "ver_productos_de_tienda":
                            output = await StoreItemsAsync(org, Str(input, "vendedor_id"), Str(input, "busqueda"), cancellationToken);
                            break;
                        case "buscar_en_ebay":
                            output = await SearchEbayAsync(Str(input, "busqueda"), Int(input, "limite") ?? 10, cancellationToken);
                            break;
                        case "calcular_ganancia":
                            output = Profit(settings, Dec(input, "costo_usd"), Dec(input, "envio_usd"), Dec(input, "precio_ml_mxn"));
                            break;
                        case "importar_link":
                            if (imported >= budget)
                            {
                                output = "No: ya llegaste al límite de este encargo. Llama a terminar.";
                                break;
                            }
                            var (linkOk, linkText) = await ImportLinkAsync(run, Str(input, "link"), Dec(input, "costo_usd"), Str(input, "titulo_ml"), Str(input, "motivo"), cancellationToken);
                            if (linkOk) imported++;
                            output = linkText;
                            break;
                        case "terminar":
                            run.Summary = Truncate(Str(input, "resumen"), 2000);
                            output = "Listo.";
                            finished = true;
                            break;
                        default:
                            output = "Herramienta desconocida.";
                            break;
                    }
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation(exception, "Agent tool {Tool} failed.", name);
                    output = "Error: " + exception.Message;
                }

                results.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = output });
            }

            run.Reviewed = reviewed.Count;
            await _context.SaveChangesAsync(cancellationToken);

            if (finished)
                return;
            if (results.Count == 0)
            {
                // Respondió solo con texto: lo usamos como resumen.
                run.Summary ??= Truncate(string.Join(" ", content.EnumerateArray()
                    .Where(b => b.GetProperty("type").GetString() == "text")
                    .Select(b => b.GetProperty("text").GetString())), 2000);
                return;
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
        }

        run.Summary ??= $"El agente llegó al máximo de pasos. Importó {run.Imported} y descartó {run.Discarded}.";
    }

    // ─────────────────────────────────────────────────────────────
    // Herramientas
    // ─────────────────────────────────────────────────────────────

    private async Task<string> ListCandidatesAsync(Guid org, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 30);
        var rows = await _context.PurchaseCandidates.AsNoTracking()
            .Where(x => x.OrganizationId == org && x.Status == PurchaseCandidateStatus.Suggested && x.ProductId == null)
            .OrderByDescending(x => x.DemandScore).ThenByDescending(x => x.EstimatedNetProfitMxn)
            .Take(limit)
            .Select(x => new
            {
                id = x.Id,
                en_mercado_libre = x.MarketName,
                precio_ml_mxn = x.MarketPriceMxn,
                demanda = x.DemandScore,
                dias_en_top = x.DaysInTop,
                tendencia = x.Trend,
                oferta_tienda = x.SourceTitle,
                tienda = x.SourceMarketplace,
                costo_usd = x.SourcePriceUsd,
                ganancia_mxn = x.EstimatedNetProfitMxn,
                margen_pct = x.EstimatedMarginPercent,
                coincidencia = x.MatchConfidence
            })
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? "No hay candidatos sin importar." : JsonSerializer.Serialize(rows);
    }

    private async Task<string> ReviewCandidateAsync(Guid org, long? id, CancellationToken cancellationToken)
    {
        var c = await _context.PurchaseCandidates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == org, cancellationToken);
        if (c is null)
            return "No existe ese candidato.";

        var similar = (await FindSimilarAsync(org, c, cancellationToken))
            .Select(p => new { id = p.Id, titulo = p.Title, estado = p.Status })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            id = c.Id,
            categoria = c.CategoryName,
            mercado_libre = new { titulo = c.MarketName, marca = c.MarketBrand, modelo = c.MarketModel, precio_mxn = c.MarketPriceMxn, mejor_posicion = c.BestPosition, dias_en_top = c.DaysInTop, tendencia = c.Trend },
            tienda = new { nombre = c.SourceMarketplace, titulo = c.SourceTitle, condicion = c.SourceCondition, precio_usd = c.SourcePriceUsd, envio_usd = c.SourceShippingUsd, busqueda_usada = c.SourceSearchQuery, coincidencia_automatica = c.MatchConfidence },
            numeros = new { costo_puesto_en_juarez_mxn = c.LandedCostMxn, precio_sugerido_mxn = c.RecommendedListingPriceMxn, ganancia_por_pieza_mxn = c.EstimatedNetProfitMxn, margen_pct = c.EstimatedMarginPercent, piezas_sugeridas = c.SuggestedQuantity },
            ya_en_catalogo = similar
        });
    }

    private async Task<(bool Ok, string Text)> ImportAsync(AgentRun run, long? id, string? reason, CancellationToken cancellationToken)
    {
        var org = run.OrganizationId;
        var candidate = await _context.PurchaseCandidates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == org, cancellationToken);
        if (candidate is null)
            return (false, "No existe ese candidato.");
        if (candidate.ProductId is not null)
            return (false, "Ya estaba importado.");
        var duplicate = (await FindSimilarAsync(org, candidate, cancellationToken)).FirstOrDefault();
        if (duplicate is not null)
            return (false, $"No lo importé: ya existe el producto {duplicate.Id} «{duplicate.Title}». Descártalo como repetido.");

        var productId = await _radar.PrepareAsync(org, candidate.Id, cancellationToken);
        _context.ChangeTracker.Clear();
        AutopilotResult? result = null;
        try
        {
            result = await _autopilot.RunAsync(org, productId, publishIfReady: false, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Autopilot failed for agent import {ProductId}.", productId);
        }

        // El piloto automático usa el mismo contexto: recargamos la corrida para seguir guardando.
        var tracked = await _context.AgentRuns.FirstAsync(r => r.Id == run.Id, cancellationToken);
        tracked.Imported = run.Imported + 1;
        run.Imported = tracked.Imported;
        _context.AgentSteps.Add(new AgentStep
        {
            AgentRunId = run.Id,
            Action = "imported",
            CandidateId = candidate.Id,
            ProductId = productId,
            Title = Truncate(candidate.MarketName, 300),
            Reason = Truncate(reason, 1000)
        });
        await _context.SaveChangesAsync(cancellationToken);

        var missing = result?.Missing.Where(m => !m.Contains("stock", StringComparison.OrdinalIgnoreCase) && !m.Contains("piezas", StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<string>();
        return (true, missing.Count == 0
            ? $"Importado como producto {productId} y preparado; solo falta tener stock para publicarlo."
            : $"Importado como producto {productId}; le falta: {string.Join(", ", missing)}.");
    }

    private async Task<string> DiscardAsync(AgentRun run, long? id, string? reason, CancellationToken cancellationToken)
    {
        var org = run.OrganizationId;
        var candidate = await _context.PurchaseCandidates.FirstOrDefaultAsync(x => x.Id == id && x.OrganizationId == org, cancellationToken);
        if (candidate is null)
            return "No existe ese candidato.";
        if (candidate.Status != PurchaseCandidateStatus.Suggested || candidate.ProductId is not null)
            return "Ese ya no está como sugerido; no lo toqué.";

        candidate.Status = PurchaseCandidateStatus.Discarded;
        candidate.Notes = Truncate("Descartado por el agente: " + reason, 1000);
        candidate.UpdatedAt = DateTime.UtcNow;
        var tracked = await _context.AgentRuns.FirstAsync(r => r.Id == run.Id, cancellationToken);
        tracked.Discarded = run.Discarded + 1;
        run.Discarded = tracked.Discarded;
        _context.AgentSteps.Add(new AgentStep
        {
            AgentRunId = run.Id,
            Action = "discarded",
            CandidateId = candidate.Id,
            Title = Truncate(candidate.MarketName, 300),
            Reason = Truncate(reason, 1000)
        });
        await _context.SaveChangesAsync(cancellationToken);
        return "Descartado.";
    }

    // ── Herramientas de encargo ──

    private string MlApiBase => _configuration["MercadoLibre:ApiBaseUrl"] is { Length: > 0 } v ? v.TrimEnd('/') : "https://api.mercadolibre.com";

    private async Task<string> SearchMercadoLibreAsync(Guid org, string? query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Falta la búsqueda.";
        var accountId = await _context.MarketplaceAccounts.AsNoTracking()
            .Where(a => a.OrganizationId == org && a.Marketplace == "MercadoLibre" && a.Status == "Connected")
            .Select(a => (int?)a.Id).FirstOrDefaultAsync(cancellationToken);
        string? token = null;
        if (accountId is not null)
        {
            try { token = await _mlAuth.GetValidAccessTokenAsync(org, accountId.Value); }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { _logger.LogInformation(exception, "No ML token for agent search."); }
        }

        var client = _httpClientFactory.CreateClient();
        async Task<JsonDocument?> GetAsync(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, MlApiBase + path);
            if (token is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode ? JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)) : null;
        }

        var q = Uri.EscapeDataString(query.Trim());
        using var search = await GetAsync($"/sites/MLM/search?q={q}&limit=15");
        if (search is not null && search.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
        {
            var rows = results.EnumerateArray().Select(r => new
            {
                titulo = Str(r, "title"),
                precio_mxn = Dec(r, "price"),
                vendidos = Int(r, "sold_quantity"),
                condicion = Str(r, "condition"),
                link = Str(r, "permalink")
            }).ToList();
            return JsonSerializer.Serialize(new { busqueda = query, resultados = rows });
        }

        // Sin búsqueda abierta: categoría probable y sus más vendidos.
        using var domains = await GetAsync($"/sites/MLM/domain_discovery/search?q={q}&limit=1");
        var categoryId = domains?.RootElement.ValueKind == JsonValueKind.Array && domains.RootElement.GetArrayLength() > 0
            ? Str(domains.RootElement[0], "category_id") : null;
        if (categoryId is null)
            return "Mercado Libre no devolvió resultados para esa búsqueda. Prueba con otras palabras.";
        var categoryName = Str(domains!.RootElement[0], "category_name");
        using var highlights = await GetAsync($"/highlights/MLM/category/{Uri.EscapeDataString(categoryId)}");
        var ids = highlights?.RootElement.TryGetProperty("content", out var content) == true && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().Where(c => Str(c, "type") == "ITEM").Select(c => Str(c, "id")).Where(x => x is not null).Take(15).ToList()
            : new List<string?>();
        var items = new List<object>();
        foreach (var id in ids)
        {
            using var item = await GetAsync($"/items/{Uri.EscapeDataString(id!)}");
            if (item is null) continue;
            items.Add(new { titulo = Str(item.RootElement, "title"), precio_mxn = Dec(item.RootElement, "price"), link = Str(item.RootElement, "permalink") });
        }
        return JsonSerializer.Serialize(new { busqueda = query, categoria = categoryName, mas_vendidos_de_la_categoria = items });
    }

    private async Task<string> StoreItemsAsync(Guid org, string? sellerId, string? query, CancellationToken cancellationToken)
    {
        var store = await _context.CompetitorStores.AsNoTracking().FirstOrDefaultAsync(c => c.OrganizationId == org && c.SellerId == sellerId, cancellationToken)
                    ?? new Web.Models.MarketInsights.CompetitorStore { OrganizationId = org, SellerId = sellerId ?? "" };
        var (items, total, error) = await _competitors.GetItemsAsync(org, store, query, 0, cancellationToken);
        if (error is not null) return error;
        return JsonSerializer.Serialize(new
        {
            tienda = store.Nickname,
            total_publicaciones = total,
            productos = items.Take(30).Select(i => new { titulo = i.Title, precio_mxn = i.Price, vendidos = i.Sold, condicion = i.Condition, envio_gratis = i.FreeShipping })
        });
    }

    private async Task<string> SearchEbayAsync(string? query, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Falta la búsqueda.";
        if (!_ebay.IsConfigured)
            return "eBay no está configurado (faltan Ebay__ClientId y Ebay__ClientSecret en Render). Dile al vendedor en el resumen.";
        var hits = await _ebay.SearchItemsAsync(query, Math.Clamp(limit, 1, 20), cancellationToken);
        if (hits.Count == 0)
            return "Sin resultados nuevos de precio fijo en eBay para esa búsqueda.";
        return JsonSerializer.Serialize(hits.Select(h => new
        {
            titulo = h.Title,
            precio_usd = h.PriceUsd,
            envio_usd = h.ShippingUsd,
            marca = h.Brand,
            condicion = h.Condition,
            link = h.ItemWebUrl
        }));
    }

    private string Profit(Web.Models.CrossBorder.CrossBorderSettings settings, decimal? costUsd, decimal? shippingUsd, decimal? priceMxn)
    {
        if (costUsd is not > 0 || priceMxn is not > 0)
            return "Faltan el costo en dólares o el precio en pesos.";
        var landed = _pricing.LandedCostMxn(costUsd.Value + (shippingUsd ?? 0), "USD", settings);
        var estimate = _pricing.CalculateProfitFromLandedCost(landed, priceMxn.Value, settings, null);
        return JsonSerializer.Serialize(new
        {
            costo_puesto_en_juarez_mxn = Math.Round(landed, 0),
            comision_ml_mxn = Math.Round(estimate.MarketplaceFeeMxn, 0),
            envio_y_cargos_mxn = Math.Round(estimate.ShippingOrFixedFeeMxn, 0),
            ganancia_por_pieza_mxn = Math.Round(estimate.NetProfitMxn, 0),
            margen_pct = Math.Round(estimate.NetMarginPercent, 1),
            cumple_minimo = estimate.NetProfitMxn >= settings.MinProfitAmountMxn
        });
    }

    private async Task<(bool Ok, string Text)> ImportLinkAsync(AgentRun run, string? link, decimal? costUsd, string? mlTitle, string? reason, CancellationToken cancellationToken)
    {
        var org = run.OrganizationId;
        if (string.IsNullOrWhiteSpace(link) || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return (false, "El link no es válido.");
        if (uri.Host.Contains("mercadoli", StringComparison.OrdinalIgnoreCase))
            return (false, "Ese link es de Mercado Libre; importa el de la tienda donde se compra.");
        link = uri.ToString();

        if (await _context.Products.AnyAsync(p => p.OrganizationId == org && p.SourceUrl == link, cancellationToken))
            return (false, "Ese link ya está en el catálogo.");
        var tokens = PartNumbers(mlTitle).ToList();
        foreach (var token in tokens.Take(3))
        {
            var pattern = "%" + token + "%";
            var existing = await _context.Products.AsNoTracking()
                .Where(p => p.OrganizationId == org && ((p.Model != null && EF.Functions.ILike(p.Model, pattern)) || (p.Title != null && EF.Functions.ILike(p.Title, pattern))))
                .Select(p => new { p.Id, p.Title }).FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
                return (false, $"No lo importé: ya existe el producto {existing.Id} «{existing.Title}».");
        }

        var userId = await _context.OrganizationMemberships.AsNoTracking()
            .Where(m => m.OrganizationId == org && m.IsActive)
            .OrderBy(m => m.Role == Web.Models.OrganizationRoles.Owner ? 0 : 1).ThenBy(m => m.CreatedAt)
            .Select(m => m.UserId).FirstOrDefaultAsync(cancellationToken);

        var extracted = await _extractor.ExtractAsync(link);
        var product = new Web.Models.Product
        {
            OrganizationId = org,
            CreatedByUserId = userId,
            SourceUrl = extracted.SourceUrl ?? link,
            SourceMarketplace = extracted.SourceMarketplace ?? "UNKNOWN",
            SourceProductId = extracted.SourceProductId,
            Title = await _titleTranslator.ToSpanishAsync(extracted.Title ?? mlTitle, cancellationToken),
            Description = extracted.Description,
            SourcePrice = costUsd is > 0 ? Math.Round(costUsd.Value, 2) : extracted.SourcePrice,
            SourceCurrency = costUsd is > 0 ? "USD" : extracted.SourceCurrency,
            SourceStock = extracted.SourceStock,
            ImageUrl = extracted.ImageUrl,
            Brand = extracted.Brand,
            Model = extracted.Model,
            SourceStatus = extracted.SourceStatus,
            LastSourceCheckAt = DateTime.UtcNow,
            Status = "Draft",
            CreatedAt = DateTime.UtcNow
        };
        _context.Products.Add(product);
        await _context.SaveChangesAsync(cancellationToken);
        var productId = product.Id;
        var title = product.Title;

        AutopilotResult? result = null;
        try
        {
            _context.ChangeTracker.Clear();
            result = await _autopilot.RunAsync(org, productId, publishIfReady: false, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Autopilot failed for agent link import {ProductId}.", productId);
        }

        _context.ChangeTracker.Clear();
        var tracked = await _context.AgentRuns.FirstAsync(r => r.Id == run.Id, cancellationToken);
        tracked.Imported = run.Imported + 1;
        run.Imported = tracked.Imported;
        _context.AgentSteps.Add(new AgentStep
        {
            AgentRunId = run.Id,
            Action = "imported",
            ProductId = productId,
            Title = Truncate(mlTitle ?? title, 300),
            Reason = Truncate(reason, 1000)
        });
        await _context.SaveChangesAsync(cancellationToken);

        var missing = result?.Missing.Where(m => !m.Contains("stock", StringComparison.OrdinalIgnoreCase) && !m.Contains("piezas", StringComparison.OrdinalIgnoreCase)).ToList() ?? new List<string>();
        return (true, missing.Count == 0
            ? $"Importado como producto {productId} y preparado."
            : $"Importado como producto {productId}; le falta: {string.Join(", ", missing)}.");
    }

    private static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d
            : v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) ? s : null
            : null;

    /// <summary>
    /// Productos del catálogo que parecen el mismo: mismo id de la tienda, mismo modelo, o el número de parte
    /// del título (ej. "252-1047", "RT1218") aparece en su título o modelo.
    /// </summary>
    private async Task<IReadOnlyList<Web.Models.Product>> FindSimilarAsync(Guid org, PurchaseCandidate c, CancellationToken cancellationToken)
    {
        var tokens = PartNumbers(c.MarketModel, c.MarketName, c.SourceTitle).Take(4).ToList();
        var query = _context.Products.AsNoTracking().Where(p => p.OrganizationId == org);
        var bySource = c.SourceProductId is null
            ? new List<Web.Models.Product>()
            : await query.Where(p => p.SourceProductId == c.SourceProductId).Take(5).ToListAsync(cancellationToken);
        var found = new List<Web.Models.Product>(bySource);
        foreach (var token in tokens)
        {
            var pattern = "%" + token + "%";
            found.AddRange(await query
                .Where(p => (p.Model != null && EF.Functions.ILike(p.Model, pattern)) || (p.Title != null && EF.Functions.ILike(p.Title, pattern)))
                .Take(5).ToListAsync(cancellationToken));
        }
        return found.GroupBy(p => p.Id).Select(g => g.First()).Take(5).ToList();
    }

    /// <summary>Números de parte: palabras con 4+ dígitos que no son años, rangos de años ni medidas.</summary>
    internal static IEnumerable<string> PartNumbers(params string?[] texts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z0-9][A-Za-z0-9\-]{3,}"))
            {
                var t = m.Value.Trim('-');
                // Al menos 4 dígitos: RT1218, 252-1047, 71439 sí; F-150, H11, A4 (modelos de vehículo o focos) no.
                if (t.Length < 4 || t.Count(char.IsDigit) < 4) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^(19|20)\d{2}(-(19|20)?\d{2})?$")) continue; // 2015, 2014-2018
                if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d+(mm|cm|in|oz|ml|lb|kg|w|v|psi)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                if (seen.Add(t)) yield return t;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────

    private async Task FinishAsync(AgentRun run, string status, string? summary, CancellationToken cancellationToken)
    {
        var tracked = await _context.AgentRuns.FirstAsync(r => r.Id == run.Id, cancellationToken);
        tracked.Status = status;
        tracked.Summary = Truncate(summary, 2000);
        tracked.Error = run.Error;
        tracked.Reviewed = Math.Max(tracked.Reviewed, run.Reviewed);
        tracked.Imported = run.Imported;
        tracked.Discarded = run.Discarded;
        tracked.Engine = run.Engine;
        tracked.FinishedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
        run.Status = status;
        run.Summary = tracked.Summary;
        run.FinishedAt = tracked.FinishedAt;
    }

    /// <summary>Reporte a Telegram cuando importó o descartó algo.</summary>
    private async Task ReportAsync(AgentRun run, CancellationToken cancellationToken)
    {
        if (run.Request is null && run.Imported == 0 && run.Discarded == 0 && run.Status != AgentRunStatus.Failed)
            return;
        try
        {
            var steps = await _context.AgentSteps.AsNoTracking().Where(s => s.AgentRunId == run.Id).OrderBy(s => s.Id).ToListAsync(cancellationToken);
            var html = new StringBuilder();
            html.Append(run.Request is null
                ? "🤖 <b>Agente de importación</b>\n"
                : $"🤖 <b>Encargo:</b> «{TelegramNotificationService.Escape(run.Request)}»\n");
            if (!string.IsNullOrWhiteSpace(run.Summary))
                html.Append(TelegramNotificationService.Escape(run.Summary)).Append("\n");
            var imported = steps.Where(s => s.Action == "imported").ToList();
            if (imported.Count > 0)
            {
                html.Append("\n✅ <b>Importó ").Append(imported.Count).Append("</b>\n");
                foreach (var s in imported.Take(8))
                    html.Append("• ").Append(TelegramNotificationService.Escape(Shorten(s.Title, 60))).Append('\n');
            }
            var discarded = steps.Where(s => s.Action == "discarded").ToList();
            if (discarded.Count > 0)
            {
                html.Append("\n✕ <b>Descartó ").Append(discarded.Count).Append("</b>\n");
                foreach (var s in discarded.Take(5))
                    html.Append("• ").Append(TelegramNotificationService.Escape(Shorten(s.Title, 40))).Append(" — ")
                        .Append(TelegramNotificationService.Escape(Shorten(s.Reason, 80))).Append('\n');
            }
            await _telegram.SendReportAsync(run.OrganizationId, html.ToString().TrimEnd(), "Ver lo que hizo", "/Agent", cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(exception, "Agent report to Telegram failed.");
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n
            : v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s) ? s : null
            : null;

    private static string? Truncate(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Shorten(string? text, int max) => Truncate(text ?? string.Empty, max) ?? string.Empty;
}
