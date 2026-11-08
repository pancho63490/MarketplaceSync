using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarketplaceSync.Services.Ai;
using MarketplaceSync.Services.Automation;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Services.MercadoLibre;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Automation;
using MarketplaceSync.Web.Models.Orders;
using MarketplaceSync.Web.Models.Questions;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Services.Notifications;

/// <summary>Lo que respondió el asistente y lo que la capa de Telegram debe mandar después.</summary>
public sealed record AssistantReply(string Text, IReadOnlyList<BotAction> Actions, bool SendLabels, bool SendPurchaseList);

/// <summary>
/// Asistente por Telegram: el vendedor escribe normal ("¿cuánto vendí hoy?", "sube el faro a 3500",
/// "pausa las balatas") y Claude usa herramientas con los datos de la organización. Los cambios en
/// Mercado Libre nunca se hacen directo: se proponen como <see cref="BotAction"/> con botón de confirmar.
/// </summary>
public sealed class TelegramAssistantService
{
    private const int MaxTurns = 6;
    private static readonly TimeSpan Memory = TimeSpan.FromMinutes(30);
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    private readonly AppDbContext _context;
    private readonly ClaudeClient _claude;
    private readonly ICrossBorderPricingService _pricing;
    private readonly PublicationEconomics _economics;
    private readonly BotActionService _actions;
    private readonly Hangfire.IBackgroundJobClient _jobs;
    private readonly ILogger<TelegramAssistantService> _logger;
    private readonly IServiceProvider _services;

    public TelegramAssistantService(
        AppDbContext context,
        ClaudeClient claude,
        ICrossBorderPricingService pricing,
        PublicationEconomics economics,
        BotActionService actions,
        Hangfire.IBackgroundJobClient jobs,
        ILogger<TelegramAssistantService> logger,
        IServiceProvider services)
    {
        _services = services;
        _context = context;
        _claude = claude;
        _pricing = pricing;
        _economics = economics;
        _actions = actions;
        _jobs = jobs;
        _logger = logger;
    }

    public bool IsConfigured => _claude.IsConfigured;

    public async Task<AssistantReply> AskAsync(Guid org, string text, CancellationToken cancellationToken = default)
    {
        text = text.Length > 1500 ? text[..1500] : text.Trim();

        // Conversación reciente (30 min) para entender seguimientos como "¿y ayer?" o "sí, súbelo".
        var since = DateTime.UtcNow - Memory;
        var history = await _context.AssistantTurns.AsNoTracking()
            .Where(t => t.OrganizationId == org && t.CreatedAt >= since)
            .OrderByDescending(t => t.Id).Take(8)
            .OrderBy(t => t.Id)
            .ToListAsync(cancellationToken);
        while (history.Count > 0 && history[0].Role != "user")
            history.RemoveAt(0);

        var messages = new JsonArray();
        foreach (var turn in history)
            messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content });
        if (messages.Count > 0 && messages[^1]!["role"]!.GetValue<string>() == "user")
            messages.RemoveAt(messages.Count - 1); // nunca dos del usuario seguidas
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });

        var actions = new List<BotAction>();
        var sendLabels = false;
        var sendPurchase = false;
        string? reply = null;

        for (var turn = 0; turn < MaxTurns && reply is null; turn++)
        {
            using var response = await _claude.SendWithToolsAsync(SystemPrompt(), messages, Tools(), 1200, cancellationToken);
            if (response is null || !response.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                reply = "No pude pensar la respuesta ahorita (la IA no respondió). Intenta de nuevo en un momento.";
                break;
            }

            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = JsonNode.Parse(content.GetRawText()) });
            var results = new JsonArray();
            var textParts = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                var type = block.GetProperty("type").GetString();
                if (type == "text")
                {
                    textParts.Append(block.GetProperty("text").GetString());
                    continue;
                }
                if (type != "tool_use")
                    continue;

                var id = block.GetProperty("id").GetString()!;
                var name = block.GetProperty("name").GetString()!;
                var input = block.TryGetProperty("input", out var i) ? i : default;
                string output;
                try
                {
                    output = name switch
                    {
                        "ventas" => await SalesAsync(org, Str(input, "periodo"), cancellationToken),
                        "buscar_publicaciones" => await FindPublicationsAsync(org, Str(input, "texto"), cancellationToken),
                        "pendientes" => await PendingAsync(org, cancellationToken),
                        "inventario" => await StockAsync(org, Str(input, "texto"), cancellationToken),
                        "proponer_precio" => await ProposePriceAsync(org, Int(input, "publicacion_id"), Dec(input, "precio"), actions, cancellationToken),
                        "proponer_precio_sugerido" => await ProposePriceAsync(org, Int(input, "publicacion_id"), null, actions, cancellationToken),
                        "proponer_pausar" => await ProposeStatusAsync(org, Int(input, "publicacion_id"), BotActionKinds.Pause, actions, cancellationToken),
                        "proponer_activar" => await ProposeStatusAsync(org, Int(input, "publicacion_id"), BotActionKinds.Activate, actions, cancellationToken),
                        "encargar_busqueda" => Enqueue(org, Str(input, "texto")),
                        "promociones" => await PromotionsAsync(org, cancellationToken),
                        "publicidad" => await AdsAsync(org, Str(input, "dias"), cancellationToken),
                        "proponer_pausar_anuncio" => await ProposeAdPauseAsync(org, Int(input, "publicacion_id"), actions, cancellationToken),
                        "proponer_descuento" => await ProposeDiscountAsync(org, Int(input, "publicacion_id"), Int(input, "porcentaje"), Int(input, "dias"), actions, cancellationToken),
                        "proponer_quitar_promocion" => await ProposeLeaveAsync(org, Int(input, "publicacion_id"), actions, cancellationToken),
                        "registrar_gasto" => await AddExpenseAsync(org, input, cancellationToken),
                        "ganancia_del_mes" => await MonthAsync(org, cancellationToken),
                        "mandar_etiquetas" => Flag(ref sendLabels, "Listo: las etiquetas se mandan en un mensaje aparte."),
                        "lista_de_compra" => Flag(ref sendPurchase, "Listo: la lista de compra se manda en un mensaje aparte."),
                        _ => "Herramienta desconocida."
                    };
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(exception, "Assistant tool {Tool} failed.", name);
                    output = "Falló al consultar: " + exception.Message;
                }

                results.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = output });
            }

            if (results.Count > 0)
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
            else
                reply = textParts.ToString().Trim();
        }

        reply = string.IsNullOrWhiteSpace(reply) ? "Listo." : reply;
        _context.AssistantTurns.Add(new AssistantTurn { OrganizationId = org, Role = "user", Content = text });
        _context.AssistantTurns.Add(new AssistantTurn { OrganizationId = org, Role = "assistant", Content = reply.Length > 4000 ? reply[..4000] : reply });
        // Se guarda solo lo reciente.
        var old = DateTime.UtcNow.AddDays(-7);
        await _context.AssistantTurns.Where(t => t.OrganizationId == org && t.CreatedAt < old).ExecuteDeleteAsync(cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        return new AssistantReply(reply, actions, sendLabels, sendPurchase);
    }

    private static string SystemPrompt() => $"""
        Eres el asistente por Telegram de un vendedor de Ciudad Juárez que compra en El Paso y vende en Mercado Libre México.
        Hablas español de México, corto y directo: máximo 6 líneas, sin tablas ni markdown (solo texto y viñetas con "•").
        Hoy es {DateTime.UtcNow.AddHours(-7):dddd d 'de' MMMM yyyy, HH:mm} (hora de Juárez).

        Reglas:
        - Usa las herramientas para cualquier dato; nunca inventes cifras, productos ni pedidos.
        - Para cambiar precio, pausar o activar usa las herramientas proponer_*: el vendedor confirma con un botón.
          Di qué propones y que lo confirme con el botón; no digas que ya está hecho.
        - Si te pide "sube/baja X a $N", primero busca la publicación con buscar_publicaciones. Si hay varias que
          coinciden, pregunta cuál (muestra 2-3 opciones con su precio) en vez de adivinar.
        - Si un precio nuevo deja pérdida o baja de la ganancia mínima, adviértelo al proponerlo.
        - Descuentos: "ponle 10% al faro por una semana" → busca la publicación y usa proponer_descuento (5% a 80%, hasta 31 días;
          di cuánto queda y cuánto gana). "Quítale el descuento" → proponer_quitar_promocion. "¿Qué campañas hay?" → promociones.
        - Publicidad: "¿cómo van mis anuncios?" → publicidad; "pausa el anuncio del faro" → busca la publicación y proponer_pausar_anuncio.
        - "Encárgale/busca productos de…" → encargar_busqueda. "Etiquetas" → mandar_etiquetas. "Qué compro" → lista_de_compra.
        - Montos en pesos salvo que digan US$. Sobre impuestos, información general y que lo confirme con su contador.
        """;

    private static JsonArray Tools() => new()
    {
        Tool("ventas", "Ventas pagadas de un periodo: pedidos, total, piezas, comisiones y lo más vendido.",
            new JsonObject { ["periodo"] = Enum("hoy", "ayer", "7d", "30d", "mes") }, "periodo"),
        Tool("buscar_publicaciones", "Busca publicaciones de Mercado Libre del vendedor por palabras del título, SKU o número MLM. Regresa id, precio, stock, estado, ganancia por pieza y precio sugerido.",
            new JsonObject { ["texto"] = Prop("string", "palabras a buscar; vacío = las de menor ganancia") }),
        Tool("pendientes", "Lo pendiente: preguntas sin responder, pedidos por enviar o atrasados, reclamos y mensajes de compradores.", new JsonObject()),
        Tool("inventario", "Stock en la app por SKU o título.", new JsonObject { ["texto"] = Prop("string", "SKU o palabras") }, "texto"),
        Tool("proponer_precio", "Propone cambiar el precio de una publicación (el vendedor confirma con botón). Regresa la ganancia a ese precio.",
            new JsonObject { ["publicacion_id"] = Prop("integer", "id de buscar_publicaciones"), ["precio"] = Prop("number", "precio nuevo en pesos") }, "publicacion_id", "precio"),
        Tool("proponer_precio_sugerido", "Propone poner el precio sugerido (el que gana lo de Costos de cruce).",
            new JsonObject { ["publicacion_id"] = Prop("integer", "id de buscar_publicaciones") }, "publicacion_id"),
        Tool("proponer_pausar", "Propone pausar una publicación.", new JsonObject { ["publicacion_id"] = Prop("integer", "id") }, "publicacion_id"),
        Tool("proponer_activar", "Propone activar una publicación pausada.", new JsonObject { ["publicacion_id"] = Prop("integer", "id") }, "publicacion_id"),
        Tool("encargar_busqueda", "El agente de importación busca productos en Mercado Libre y eBay e importa lo que convenga.",
            new JsonObject { ["texto"] = Prop("string", "qué buscar, ej. 'faros Ford Ranger 2019'") }, "texto"),
        Tool("registrar_gasto", "Anota un gasto del negocio que no pasa por Mercado Libre (gasolina, cruce, cajas, Render…). Úsalo cuando diga 'gasté', 'pagué', 'anota un gasto'.",
            new JsonObject
            {
                ["monto"] = Prop("number", "monto"),
                ["moneda"] = Enum("MXN", "USD"),
                ["descripcion"] = Prop("string", "qué fue, corto: 'Cajas medianas', 'Gasolina viaje a El Paso'"),
                ["mensual"] = Prop("boolean", "true si se repite cada mes (Render, internet)")
            }, "monto", "descripcion"),
        Tool("promociones", "Campañas de Mercado Libre a las que invitan al vendedor (cuántas publicaciones pueden entrar y cuántas siguen ganando) y publicaciones con descuento ahora.", new JsonObject()),
        Tool("proponer_descuento", "Propone un descuento individual (sale tachado en Mercado Libre). El vendedor confirma con botón. Regresa precio con descuento y ganancia.",
            new JsonObject
            {
                ["publicacion_id"] = Prop("integer", "id de buscar_publicaciones"),
                ["porcentaje"] = Prop("integer", "5 a 80"),
                ["dias"] = Prop("integer", "1 a 31; 7 si no dice")
            }, "publicacion_id", "porcentaje"),
        Tool("proponer_quitar_promocion", "Propone sacar una publicación de su descuento o campaña (vuelve a su precio normal).",
            new JsonObject { ["publicacion_id"] = Prop("integer", "id") }, "publicacion_id"),
        Tool("publicidad", "Mercado Ads: gasto, ventas con anuncios, ACOS, ganancia con anuncios y los anuncios que pierden.",
            new JsonObject { ["dias"] = Enum("7", "30", "90") }),
        Tool("proponer_pausar_anuncio", "Propone pausar el anuncio de Mercado Ads de una publicación (no pausa la publicación).",
            new JsonObject { ["publicacion_id"] = Prop("integer", "id") }, "publicacion_id"),
        Tool("ganancia_del_mes", "Ganancia neta del mes: ventas, ganancia de las ventas, gastos y neta.", new JsonObject()),
        Tool("mandar_etiquetas", "Manda ahora el PDF de etiquetas y la lista de empaque de los pedidos por enviar.", new JsonObject()),
        Tool("lista_de_compra", "Manda ahora la lista de qué recomprar en EE.UU. para aprobarla.", new JsonObject())
    };

    // ── Herramientas ──

    private async Task<string> SalesAsync(Guid org, string? period, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-7));
        var (start, end, label) = period switch
        {
            "ayer" => (today.AddDays(-1), today, "ayer"),
            "7d" => (today.AddDays(-6), today.AddDays(1), "últimos 7 días"),
            "30d" => (today.AddDays(-29), today.AddDays(1), "últimos 30 días"),
            "mes" => (new DateOnly(today.Year, today.Month, 1), today.AddDays(1), "este mes"),
            _ => (today, today.AddDays(1), "hoy")
        };
        DateTime Utc(DateOnly d) => DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MinValue).AddHours(7), DateTimeKind.Utc);
        var from = Utc(start);
        var to = Utc(end);

        var orders = await _context.SalesOrders.AsNoTracking()
            .Where(o => o.OrganizationId == org && o.Status == "paid" && o.DateCreated >= from && o.DateCreated < to)
            .Select(o => new { o.TotalAmount, o.RealProfitMxn, Items = o.Items.Select(i => new { i.Title, i.Quantity, i.SaleFee }) })
            .ToListAsync(cancellationToken);
        var top = orders.SelectMany(o => o.Items)
            .GroupBy(i => i.Title ?? "(sin título)")
            .Select(g => new { titulo = Short(g.Key, 60), piezas = g.Sum(i => i.Quantity) })
            .OrderByDescending(x => x.piezas).Take(5);
        return JsonSerializer.Serialize(new
        {
            periodo = label,
            pedidos = orders.Count,
            total = Money(orders.Sum(o => o.TotalAmount)),
            piezas = orders.Sum(o => o.Items.Sum(i => i.Quantity)),
            comisiones_ml = Money(orders.Sum(o => o.Items.Sum(i => (i.SaleFee ?? 0) * i.Quantity))),
            ganancia_real = orders.Any(o => o.RealProfitMxn.HasValue) ? Money(orders.Sum(o => o.RealProfitMxn ?? 0)) : "sin calcular aún",
            ventas_con_ganancia_calculada = orders.Count(o => o.RealProfitMxn.HasValue),
            ventas_con_perdida = orders.Count(o => o.RealProfitMxn < 0),
            mas_vendido = top
        });
    }

    private async Task<string> FindPublicationsAsync(Guid org, string? text, CancellationToken cancellationToken)
    {
        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var query = _context.MarketplacePublications.AsNoTracking()
            .Include(p => p.Product).ThenInclude(p => p.Variants)
            .Where(p => p.Product.OrganizationId == org && p.Marketplace == "MercadoLibre" && p.IsPublished && p.ExternalItemId != null);

        var words = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length >= 2).Take(6).ToList();
        foreach (var word in words)
        {
            var pattern = $"%{word}%";
            query = query.Where(p => EF.Functions.ILike(p.Product.Title ?? "", pattern) ||
                                     EF.Functions.ILike(p.ExternalItemId!, pattern) ||
                                     p.Product.Variants.Any(v => EF.Functions.ILike(v.Sku, pattern)));
        }

        var list = await query.OrderByDescending(p => p.LastSyncedAt).Take(words.Count == 0 ? 60 : 10).ToListAsync(cancellationToken);
        var rows = list.Select(p =>
        {
            var money = _economics.Calculate(p, p.Product, settings);
            return new
            {
                publicacion_id = p.Id,
                mlm = p.ExternalItemId,
                titulo = Short(p.Product.Title, 70),
                precio = p.Price,
                stock_ml = p.Stock,
                estado = p.Status,
                ganancia_por_pieza = money?.Profit.NetProfitMxn,
                gana_lo_minimo = money?.Profit.IsProfitable,
                precio_sugerido = money?.NeedsRaise == true ? money.SuggestedPriceMxn : (decimal?)null,
                costo = money is null ? "sin costo capturado" : null
            };
        }).ToList();
        if (words.Count == 0)
            rows = rows.OrderBy(r => r.ganancia_por_pieza ?? decimal.MaxValue).Take(8).ToList();
        return rows.Count == 0 ? "No encontré publicaciones con eso." : JsonSerializer.Serialize(rows);
    }

    private async Task<string> PendingAsync(Guid org, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var questions = await _context.BuyerQuestions.CountAsync(q => q.OrganizationId == org && q.Status == BuyerQuestionStatus.Unanswered, cancellationToken);
        var toShip = await _context.SalesOrders.Where(o => o.OrganizationId == org).ToShip().CountAsync(cancellationToken);
        var late = await _context.SalesOrders.Where(o => o.OrganizationId == org).LateToShip(now).CountAsync(cancellationToken);
        var claims = await _context.OrderClaims.CountAsync(c => c.OrganizationId == org && c.Status == "opened", cancellationToken);
        var recentFrom = now.AddDays(-45);
        var recent = await _context.OrderMessages.AsNoTracking()
            .Where(m => m.OrganizationId == org && m.SentAt >= recentFrom)
            .Select(m => new { m.PackId, m.FromSeller, m.SentAt })
            .ToListAsync(cancellationToken);
        var messages = recent.GroupBy(m => m.PackId).Count(g => !g.OrderByDescending(m => m.SentAt).First().FromSeller);
        return JsonSerializer.Serialize(new
        {
            preguntas_sin_responder = questions,
            pedidos_por_enviar = toShip,
            pedidos_atrasados = late,
            reclamos_abiertos = claims,
            compradores_esperando_respuesta = messages
        });
    }

    private async Task<string> StockAsync(Guid org, string? text, CancellationToken cancellationToken)
    {
        var pattern = $"%{(text ?? string.Empty).Trim()}%";
        var rows = await _context.ProductVariants.AsNoTracking()
            .Where(v => v.OrganizationId == org && v.IsActive &&
                        (EF.Functions.ILike(v.Sku, pattern) || EF.Functions.ILike(v.Product.Title ?? "", pattern)))
            .OrderBy(v => v.Stock).Take(10)
            .Select(v => new { sku = v.Sku, titulo = v.Product.Title, stock = v.Stock, minimo = v.LowStockThreshold })
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? "No encontré ese SKU o producto." : JsonSerializer.Serialize(rows.Select(r => new { r.sku, titulo = Short(r.titulo, 60), r.stock, r.minimo }));
    }

    private async Task<string> ProposePriceAsync(Guid org, int? publicationId, decimal? price, List<BotAction> actions, CancellationToken cancellationToken)
    {
        var publication = await LoadAsync(org, publicationId, cancellationToken);
        if (publication is null)
            return "No encontré esa publicación; búscala primero.";

        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var current = _economics.Calculate(publication, publication.Product, settings);
        var target = price ?? current?.SuggestedPriceMxn;
        if (target is not > 0)
            return "No puedo calcular el precio sugerido: falta el costo del producto. Pide un precio concreto.";
        target = Math.Round(target.Value, 0, MidpointRounding.AwayFromZero);
        if (publication.Price is > 0 && Math.Abs(target.Value - publication.Price.Value) / publication.Price.Value > 1.0m)
            return $"Ese cambio es de más del doble o menos de la mitad del precio actual ({Money(publication.Price)}). Confírmame el número antes de proponerlo.";

        var atNew = _economics.Calculate(publication, publication.Product, settings, target);
        var title = Short(publication.Product.Title, 50);
        var description = $"Cambiar «{title}» de {Money(publication.Price)} a {Money(target)}" +
                          (atNew is null ? "" : atNew.Profit.NetProfitMxn < 0
                              ? $" (aún perderías {Money(-atNew.Profit.NetProfitMxn)} por pieza)"
                              : $" (ganarías {Money(atNew.Profit.NetProfitMxn)} por pieza)");
        var action = await _actions.ProposeAsync(org, BotActionKinds.Price, publication.Id, target, description, "assistant", cancellationToken);
        actions.Add(action);
        return JsonSerializer.Serialize(new
        {
            propuesto = description,
            ganancia_a_ese_precio = atNew?.Profit.NetProfitMxn,
            deja_perdida = atNew?.Profit.NetProfitMxn < 0,
            gana_lo_minimo = atNew?.Profit.IsProfitable,
            nota = "El vendedor debe confirmarlo con el botón."
        });
    }

    private async Task<string> ProposeStatusAsync(Guid org, int? publicationId, string kind, List<BotAction> actions, CancellationToken cancellationToken)
    {
        var publication = await LoadAsync(org, publicationId, cancellationToken);
        if (publication is null)
            return "No encontré esa publicación; búscala primero.";
        var verb = kind == BotActionKinds.Pause ? "Pausar" : "Activar";
        var description = $"{verb} «{Short(publication.Product.Title, 60)}» ({publication.ExternalItemId})";
        var action = await _actions.ProposeAsync(org, kind, publication.Id, null, description, "assistant", cancellationToken);
        actions.Add(action);
        return $"Propuesto: {description}. El vendedor debe confirmarlo con el botón.";
    }

    private async Task<string> PromotionsAsync(Guid org, CancellationToken cancellationToken)
    {
        var promotions = _services.GetRequiredService<PromotionService>();
        var campaigns = await promotions.CampaignsAsync(org, cancellationToken);
        var discounted = await _context.MarketplacePublications.AsNoTracking()
            .Where(p => p.Product.OrganizationId == org && p.PromotionType != null)
            .Select(p => new { p.Id, p.Product.Title, p.PromotionType, p.PromotionPrice, p.PromotionOriginalPrice, p.PromotionEndsAt })
            .Take(15)
            .ToListAsync(cancellationToken);
        return JsonSerializer.Serialize(new
        {
            campanas = campaigns.Select(c => new
            {
                nombre = c.Promotion.Name,
                tipo = c.Promotion.TypeLabel,
                entrar_hasta = c.Promotion.Deadline?.AddHours(-7).ToString("d MMM", CultureInfo.GetCultureInfo("es-MX")),
                invitadas = c.Candidates,
                siguen_ganando = c.CandidatesThatEarn,
                ya_participan = c.Joined,
                ejemplos = c.Rows.Where(r => r.Offer.IsCandidate).Take(4).Select(r => new
                {
                    titulo = Short(r.Title, 50),
                    precio_hoy = Money(r.CurrentPrice),
                    precio_campana = Money(r.ProposedPrice),
                    ganancia = r.CostKnown ? Money(r.Profit) : "sin costo"
                })
            }),
            con_descuento = discounted.Select(d => new
            {
                publicacion_id = d.Id,
                titulo = Short(d.Title, 50),
                tipo = PromotionService.TypeLabel(d.PromotionType),
                precio = Money(d.PromotionPrice),
                antes = Money(d.PromotionOriginalPrice),
                termina = d.PromotionEndsAt?.AddHours(-7).ToString("d MMM", CultureInfo.GetCultureInfo("es-MX"))
            }),
            nota = "Para entrar a una campaña: botón en el aviso de Telegram (/promos) o en la app (/Promotions)."
        });
    }

    private async Task<string> AdsAsync(Guid org, string? days, CancellationToken cancellationToken)
    {
        var d = int.TryParse(days, out var n) ? n : 7;
        var report = await _services.GetRequiredService<AdsService>().ReportAsync(org, d, cancellationToken);
        if (!report.Enabled)
            return report.Error ?? "Sin Mercado Ads.";
        return JsonSerializer.Serialize(new
        {
            dias = report.Days,
            gasto = Money(report.Cost),
            ventas_con_anuncios = Money(report.Sales),
            acos = report.Acos,
            ganancia_con_anuncios = Money(report.Profit),
            campanas = report.Campaigns.Select(c => new { nombre = c.Name, estado = c.Status, gasto = Money(c.Metrics.Cost), acos = c.Metrics.Acos, acos_objetivo = c.AcosTarget, acos_maximo = c.BreakEvenAcos }),
            anuncios_que_pierden = report.Ads.Where(a => a.Verdict == "bad" && a.Metrics.Cost > 0).Take(6).Select(a => new
            {
                publicacion_id = a.PublicationId,
                titulo = Short(a.Title, 50),
                gasto = Money(a.Metrics.Cost),
                vendio = a.Metrics.DirectUnits,
                acos = a.RealAcos,
                acos_maximo = a.BreakEvenAcos,
                ganancia = a.Profit is { } p ? Money(p) : "sin costo"
            }),
            nota = "ACOS máximo = ganancia por pieza ÷ precio. Para pausar un anuncio usa proponer_pausar_anuncio."
        });
    }

    private async Task<string> ProposeAdPauseAsync(Guid org, int? publicationId, List<BotAction> actions, CancellationToken cancellationToken)
    {
        var publication = await LoadAsync(org, publicationId, cancellationToken);
        if (publication?.ExternalItemId is null || publication.MarketplaceAccountId is null)
            return "No encontré esa publicación; búscala primero.";
        var description = $"Pausar el anuncio de «{Short(publication.Product.Title, 60)}» (la publicación sigue activa)";
        var action = await _actions.ProposeAsync(org, BotActionKinds.AdPause, publication.Id, null, description, "assistant", cancellationToken);
        action.Payload = JsonSerializer.Serialize(new { accountId = publication.MarketplaceAccountId.Value, itemId = publication.ExternalItemId });
        await _context.SaveChangesAsync(cancellationToken);
        actions.Add(action);
        return $"Propuesto: {description}. El vendedor debe confirmarlo con el botón.";
    }

    private async Task<string> ProposeDiscountAsync(Guid org, int? publicationId, int? percent, int? days, List<BotAction> actions, CancellationToken cancellationToken)
    {
        var publication = await LoadAsync(org, publicationId, cancellationToken);
        if (publication is null)
            return "No encontré esa publicación; búscala primero.";
        if (percent is not (>= PromotionService.MinDiscount and <= PromotionService.MaxDiscount))
            return $"El descuento debe ser de {PromotionService.MinDiscount}% a {PromotionService.MaxDiscount}%.";
        if (publication.PromotionType is not null)
            return $"Ya tiene una promoción ({PromotionService.TypeLabel(publication.PromotionType)}) a {Money(publication.PromotionPrice)}. Primero hay que quitarla (proponer_quitar_promocion).";
        var d = Math.Clamp(days ?? 7, 1, PromotionService.MaxDays);
        var basePrice = PromotionService.BasePrice(publication);
        if (basePrice is not > 0)
            return "Esa publicación no tiene precio.";

        var settings = await _pricing.GetOrCreateSettingsAsync(org, cancellationToken);
        var price = PromotionService.DiscountedPrice(basePrice.Value, percent.Value);
        var money = _economics.Calculate(publication, publication.Product, settings, price);
        var description = $"Poner {percent}% de descuento a «{Short(publication.Product.Title, 45)}» por {d} {(d == 1 ? "día" : "días")}: {Money(basePrice)} → {Money(price)}" +
                          (money is null ? "" : money.Profit.NetProfitMxn < 0
                              ? $" (perderías {Money(-money.Profit.NetProfitMxn)} por pieza)"
                              : $" (ganarías {Money(money.Profit.NetProfitMxn)} por pieza)");
        var action = await _actions.ProposeAsync(org, BotActionKinds.Discount, publication.Id, percent, description, "assistant", cancellationToken);
        action.Payload = JsonSerializer.Serialize(new { days = d });
        await _context.SaveChangesAsync(cancellationToken);
        actions.Add(action);
        return JsonSerializer.Serialize(new
        {
            propuesto = description,
            ganancia_con_descuento = money?.Profit.NetProfitMxn,
            gana_lo_minimo = money is not null && money.Profit.NetProfitMxn >= settings.MinProfitAmountMxn,
            nota = "El vendedor debe confirmarlo con el botón. Mercado Libre pide reputación verde y producto nuevo."
        });
    }

    private async Task<string> ProposeLeaveAsync(Guid org, int? publicationId, List<BotAction> actions, CancellationToken cancellationToken)
    {
        var publication = await LoadAsync(org, publicationId, cancellationToken);
        if (publication is null)
            return "No encontré esa publicación; búscala primero.";
        if (publication.PromotionType is null)
            return "Según lo último que leí, esa publicación no tiene promoción.";
        var description = $"Quitar la promoción ({PromotionService.TypeLabel(publication.PromotionType)}) a «{Short(publication.Product.Title, 50)}» " +
                          $"(vuelve de {Money(publication.PromotionPrice)} a {Money(PromotionService.BasePrice(publication))})";
        var action = await _actions.ProposeAsync(org, BotActionKinds.DiscountRemove, publication.Id, null, description, "assistant", cancellationToken);
        actions.Add(action);
        return $"Propuesto: {description}. El vendedor debe confirmarlo con el botón.";
    }

    private async Task<string> AddExpenseAsync(Guid org, JsonElement input, CancellationToken cancellationToken)
    {
        var amount = Dec(input, "monto");
        var description = Str(input, "descripcion");
        if (amount is not > 0 || string.IsNullOrWhiteSpace(description))
            return "Falta el monto o qué fue.";
        var monthly = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("mensual", out var m) && m.ValueKind == JsonValueKind.True;
        var finance = _services.GetRequiredService<MarketplaceSync.Services.Finance.FinanceReportService>();
        var expense = await finance.AddExpenseAsync(org, MarketplaceSync.Services.Finance.FinanceReportService.TodayJuarez,
            MarketplaceSync.Web.Models.Finance.ExpenseCategories.Guess(description), description, amount.Value, Str(input, "moneda") ?? "MXN", monthly, "telegram", cancellationToken);
        return JsonSerializer.Serialize(new
        {
            anotado = expense.Description,
            monto_mxn = Money(expense.AmountMxn),
            categoria = MarketplaceSync.Web.Models.Finance.ExpenseCategories.Label(expense.Category),
            mensual = expense.IsMonthly
        });
    }

    private async Task<string> MonthAsync(Guid org, CancellationToken cancellationToken)
    {
        var finance = _services.GetRequiredService<MarketplaceSync.Services.Finance.FinanceReportService>();
        var today = MarketplaceSync.Services.Finance.FinanceReportService.TodayJuarez;
        var r = await finance.MonthAsync(org, today.Year, today.Month, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            mes = r.Title,
            ventas = Money(r.Charged),
            numero_de_ventas = r.Sales.Count,
            ganancia_de_ventas = Money(r.SalesProfit),
            ventas_sin_costo = r.SalesWithoutCost,
            gastos = Money(r.ExpensesTotal),
            ganancia_neta = Money(r.NetProfit),
            retenciones = Money(r.Tax),
            comisiones = Money(r.Fees),
            envios = Money(r.Shipping)
        });
    }

    private string Enqueue(Guid org, string? request)
    {
        if (string.IsNullOrWhiteSpace(request) || request.Trim().Length < 3)
            return "Falta qué buscar.";
        var value = request.Trim().Length > 500 ? request.Trim()[..500] : request.Trim();
        _jobs.Enqueue<MarketplaceSync.Services.Jobs.MarketplaceJobsService>(job => job.RunImportAgentRequest(org, value));
        return "Encargo enviado al agente; avisará por Telegram en unos minutos con lo que importó.";
    }

    private static string Flag(ref bool flag, string message)
    {
        flag = true;
        return message;
    }

    private Task<MarketplaceSync.Web.Models.Marketplace.MarketplacePublication?> LoadAsync(Guid org, int? publicationId, CancellationToken cancellationToken) =>
        publicationId is null
            ? Task.FromResult<MarketplaceSync.Web.Models.Marketplace.MarketplacePublication?>(null)
            : _context.MarketplacePublications.AsNoTracking()
                .Include(p => p.Product)
                .FirstOrDefaultAsync(p => p.Id == publicationId && p.Product.OrganizationId == org && p.ExternalItemId != null, cancellationToken);

    // ── Ayudas ──

    private static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["input_schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray())
        }
    };

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private static JsonObject Enum(params string[] values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray())
    };

    private static string? Str(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i
            : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s) ? s : null
            : null;

    private static decimal? Dec(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d
            : v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString()?.Replace("$", "").Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) ? s : null
            : null;

    private static string Money(decimal? value) => BotActionService.Money(value);

    private static string Short(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(sin título)";
        value = value.Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }
}
