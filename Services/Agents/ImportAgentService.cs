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

    public ImportAgentService(
        AppDbContext context,
        ClaudeClient claude,
        IPurchaseRadarService radar,
        IListingAutopilotService autopilot,
        ICrossBorderPricingService pricing,
        RadarAutoPrepareService rules,
        TelegramNotificationService telegram,
        ILogger<ImportAgentService> logger)
    {
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
    public async Task<AgentRun> RunAsync(Guid organizationId, string trigger, CancellationToken cancellationToken = default)
    {
        var settings = await _pricing.GetOrCreateSettingsAsync(organizationId, cancellationToken);
        var run = new AgentRun { OrganizationId = organizationId, Trigger = trigger, Engine = _claude.IsConfigured ? "ai" : "rules" };
        _context.AgentRuns.Add(run);
        await _context.SaveChangesAsync(cancellationToken);

        try
        {
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
                ["content"] = $"Hoy es {DateTime.UtcNow.AddHours(-7):dd/MM/yyyy}. Revisa el Radar e importa lo que valga la pena (máximo {budget})."
            }
        };
        var imported = 0;
        var reviewed = new HashSet<long>();

        for (var turn = 0; turn < MaxTurns; turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await _claude.SendWithToolsAsync(SystemPrompt, messages, Tools, 2000, cancellationToken)
                                 ?? throw new InvalidOperationException("Claude no respondió.");
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
        if (run.Imported == 0 && run.Discarded == 0 && run.Status != AgentRunStatus.Failed)
            return;
        try
        {
            var steps = await _context.AgentSteps.AsNoTracking().Where(s => s.AgentRunId == run.Id).OrderBy(s => s.Id).ToListAsync(cancellationToken);
            var html = new StringBuilder();
            html.Append("🤖 <b>Agente de importación</b>\n");
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
