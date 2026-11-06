using System.Globalization;
using System.Text;
using MarketplaceSync.Services.Ai;
using MarketplaceSync.Services.Interfaces;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Purchasing;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

/// <summary>
/// Asistente de chat: responde preguntas sobre el negocio con los datos de la organización
/// (catálogo, qué comprar, inventario, pedidos y costos) y explica cómo usar la app.
/// </summary>
[Authorize]
[Route("Assistant")]
public sealed class AssistantController : Controller
{
    private const int MaxTurns = 12;
    private const int MaxTurnLength = 2000;
    private const string DefaultAssistantModel = "claude-sonnet-5-5";
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    private readonly AppDbContext _context;
    private readonly IOrganizationContext _organizationContext;
    private readonly IPurchaseRadarService _radarService;
    private readonly ICrossBorderPricingService _pricingService;
    private readonly ClaudeClient _claude;
    private readonly IConfiguration _configuration;

    public AssistantController(
        AppDbContext context,
        IOrganizationContext organizationContext,
        IPurchaseRadarService radarService,
        ICrossBorderPricingService pricingService,
        ClaudeClient claude,
        IConfiguration configuration)
    {
        _context = context;
        _organizationContext = organizationContext;
        _radarService = radarService;
        _pricingService = pricingService;
        _claude = claude;
        _configuration = configuration;
    }

    public sealed record AskRequest(List<ChatTurn>? Messages);

    [HttpPost("Ask")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ask([FromBody] AskRequest request, CancellationToken cancellationToken)
    {
        if (!_claude.IsConfigured)
        {
            return Json(new
            {
                configured = false,
                reply = "Para usar el asistente agrega la variable `Anthropic__ApiKey` en Render (Environment) y vuelve a desplegar."
            });
        }

        // Conversación saneada: turnos alternados que empiezan por el usuario.
        var turns = (request.Messages ?? new List<ChatTurn>())
            .Where(t => !string.IsNullOrWhiteSpace(t.Content))
            .Select(t => new ChatTurn(
                t.Role == "assistant" ? "assistant" : "user",
                t.Content.Length > MaxTurnLength ? t.Content[..MaxTurnLength] : t.Content.Trim()))
            .TakeLast(MaxTurns)
            .ToList();
        while (turns.Count > 0 && turns[0].Role != "user")
            turns.RemoveAt(0);
        if (turns.Count == 0 || turns[^1].Role != "user")
            return BadRequest(new { reply = "Escribe una pregunta." });

        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var context = await BuildBusinessContextAsync(organizationId, cancellationToken);

        var system = $"""
            Eres el asistente de MarketplaceSync, la herramienta de un vendedor de Ciudad Juárez que compra productos
            (sobre todo autopartes) en El Paso, Texas, y los vende en Mercado Libre México.
            Respondes en español de México, de forma breve y práctica (máximo 8 líneas o una lista corta).

            Reglas:
            - Usa solo los datos del bloque DATOS. Si un dato no está, dilo y explica dónde verlo en la app.
            - No inventes cifras, productos ni pedidos. Las cantidades de dinero están en pesos (MXN) salvo que digan US$.
            - Cuando recomiendes hacer algo en la app, incluye el enlace en formato [texto](/ruta) usando las rutas de PANTALLAS.
            - Sobre impuestos o temas legales da información general y sugiere confirmarlo con su contador o agente aduanal.
            - La demanda viene del ranking de Mercado Libre por categoría, no de unidades vendidas exactas.

            PANTALLAS:
            - Hoy (pendientes): /
            - Qué comprar en EE.UU.: /Purchasing  (pestañas: ?tab=review, ?tab=restock, ?tab=transit)
            - Categorías a vigilar (la app explora sola y prepara los mejores): /WatchedCategories
            - Más vendidos en México: /MarketInsights
            - Costos de cruce: /CrossBorder
            - Productos: /Products  (filtros: ?stage=Draft, ?stage=NeedsReview, ?stage=Ready)
            - Publicados en Mercado Libre con ganancia y stock: /Publications  (filtros: ?filter=loss, ?filter=nocost, ?filter=mismatch)
            - Agente de importación (revisa cada mañana el Radar con IA, comprueba que la oferta sea la misma pieza y que no esté repetida, importa y prepara hasta el máximo semanal, descarta con razón y avisa por Telegram; botón Ejecutar ahora): /Agent
            - Métricas y KPIs (ventas, ganancia estimada, ticket promedio, comisiones, visitas, conversión, cancelaciones, reputación con límites de verde y MercadoLíder, tiempo de respuesta a preguntas, lo más vendido y publicaciones con visitas sin ventas): /Metrics  (periodo: ?days=7, 30 o 90)
            - Editar una publicación sin entrar a Mercado Libre (título, precio, stock, fotos, descripción, ficha técnica, garantía, tipo, pausar/activar/finalizar/republicar): Publicados → Editar (/Publications/Edit/NÚMERO); cambiar precio o pausar varias a la vez: selecciónalas en Publicados
            - Importar producto con IA: /Products/CreateFromUrl
            - Inventario (incluye caducidades por lote): /Inventory
            - Pedidos: /Orders
            - Avisos por Telegram (preguntas, ventas, agotados y resumen; contestar desde Telegram): /Notifications
            - Preguntas de compradores (con respuesta sugerida por IA): /Questions  (?tab=answered)
            - Cuentas de Mercado Libre: /MercadoLibre/Status

            DATOS (actualizados al {DateTime.UtcNow.AddHours(-7):dd/MM/yyyy HH:mm}, hora de Juárez):
            {context}
            """;

        var model = _configuration["Anthropic:AssistantModel"];
        var reply = await _claude.ChatAsync(
            system,
            turns,
            900,
            0.3,
            string.IsNullOrWhiteSpace(model) ? DefaultAssistantModel : model,
            cancellationToken);

        return Json(new
        {
            configured = true,
            reply = reply ?? "No pude responder en este momento. Intenta de nuevo en unos segundos."
        });
    }

    /// <summary>Resumen compacto del negocio para el asistente (sin datos personales de compradores).</summary>
    private async Task<string> BuildBusinessContextAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

        var organizationName = await _context.Organizations
            .Where(x => x.Id == organizationId)
            .Select(x => x.Name)
            .FirstOrDefaultAsync(cancellationToken);
        sb.AppendLine($"Negocio: {organizationName}");

        // Catálogo
        var products = await _context.Products
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .Select(x => new
            {
                x.Id,
                x.Title,
                x.Status,
                x.SourceMarketplace,
                x.SourcePrice,
                x.SourceCurrency,
                Published = x.MarketplacePublications.Any(p => p.Marketplace == "MercadoLibre" && p.IsPublished),
                Notes = x.MarketplacePublications.Select(p => p.AutopilotNotes).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        sb.AppendLine($"Productos: {products.Count} en total, {products.Count(p => p.Published)} publicados en Mercado Libre, " +
                      $"{products.Count(p => !p.Published && p.Status == "ReadyToPublish")} listos para publicar, " +
                      $"{products.Count(p => !p.Published && (p.Status == "Draft" || p.Status == "NeedsReview"))} por preparar.");
        foreach (var p in products.Where(p => !p.Published).Take(10))
        {
            sb.AppendLine($"  - #{p.Id} {Short(p.Title, 70)} | {p.Status} | {p.SourceMarketplace} {(p.SourcePrice.HasValue ? p.SourcePrice.Value.ToString("0.00", CultureInfo.InvariantCulture) + " " + p.SourceCurrency : "")}" +
                          (string.IsNullOrWhiteSpace(p.Notes) ? "" : $" | {Short(p.Notes, 120)}"));
        }

        // Qué comprar
        var candidates = await _context.PurchaseCandidates
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.DemandScore > 0)
            .OrderByDescending(x => x.DemandScore)
            .ToListAsync(cancellationToken);
        var toBuy = candidates.Where(x => x.Status == PurchaseCandidateStatus.Suggested).Take(8).ToList();
        sb.AppendLine($"Qué comprar: {toBuy.Count} listos, {candidates.Count(x => x.Status is PurchaseCandidateStatus.NoSource or PurchaseCandidateStatus.LowMargin)} por revisar, " +
                      $"{await _context.PurchaseCandidates.CountAsync(x => x.OrganizationId == organizationId && x.Status == PurchaseCandidateStatus.Purchased, cancellationToken)} compras en camino.");
        foreach (var c in toBuy)
        {
            sb.AppendLine($"  - {Short(c.MarketName, 70)} | demanda {c.DemandScore}/100 ({c.Trend}) | comprar en {c.SourceMarketplace} a US${c.SourcePriceUsd:0.00} | " +
                          $"vender a {Money(c.RecommendedListingPriceMxn)} | gana {Money(c.EstimatedNetProfitMxn)}/pieza | sugerido {c.SuggestedQuantity} pzs");
        }

        // Inventario y resurtido
        var restock = await _radarService.GetRestockSuggestionsAsync(organizationId, cancellationToken);
        var variants = await _context.ProductVariants
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive)
            .Select(x => new { x.Sku, x.Stock, x.LowStockThreshold })
            .ToListAsync(cancellationToken);
        sb.AppendLine($"Inventario: {variants.Count} SKU, {variants.Sum(v => Math.Max(0, v.Stock))} piezas, " +
                      $"{variants.Count(v => v.Stock <= 0)} agotados, {variants.Count(v => v.Stock > 0 && v.Stock <= v.LowStockThreshold)} con stock bajo.");
        foreach (var r in restock.Take(5))
            sb.AppendLine($"  - Resurtir {r.Sku} {Short(r.Title, 60)}: vendió {r.UnitsSold30Days} en 30 días, quedan {r.CurrentStock}, comprar {r.SuggestedQuantity}");

        // Caducidades (lotes)
        var expiryLimit = MarketplaceSync.Services.Inventory.InventoryLotService.Today.AddDays(30);
        var lots = await _context.InventoryLots
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.WrittenOffAt == null && x.QuantityRemaining > 0 && x.ExpiresOn <= expiryLimit)
            .OrderBy(x => x.ExpiresOn)
            .Take(5)
            .Select(x => new { x.ProductVariant.Sku, x.ExpiresOn, x.QuantityRemaining })
            .ToListAsync(cancellationToken);
        if (lots.Count > 0)
        {
            sb.AppendLine("Lotes que caducan en 30 días o ya caducaron:");
            foreach (var lot in lots)
                sb.AppendLine($"  - {lot.Sku}: {lot.QuantityRemaining} pzs, caduca {lot.ExpiresOn:dd/MM/yyyy}");
        }

        // Preguntas de compradores (solo el conteo y la más antigua; el texto puede traer datos personales)
        var openQuestions = await _context.BuyerQuestions
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Status == MarketplaceSync.Web.Models.Questions.BuyerQuestionStatus.Unanswered)
            .Select(x => x.DateCreated)
            .ToListAsync(cancellationToken);
        sb.AppendLine(openQuestions.Count == 0
            ? "Preguntas sin responder: 0."
            : $"Preguntas sin responder: {openQuestions.Count}; la más antigua tiene {(int)(DateTime.UtcNow - openQuestions.Min()).TotalHours} h.");

        // Pedidos (sin datos del comprador)
        var since = DateTime.UtcNow.AddDays(-30);
        var orders = await _context.SalesOrders
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.DateCreated >= since)
            .Select(x => new { x.Status, x.PaidAmount, x.DateCreated, Items = x.Items.Select(i => i.Title).ToList() })
            .ToListAsync(cancellationToken);
        var valid = orders.Where(o => o.Status != "cancelled" && o.Status != "invalid").ToList();
        sb.AppendLine($"Pedidos últimos 30 días: {valid.Count}, ventas {Money(valid.Sum(o => o.PaidAmount))}, " +
                      $"{orders.Count(o => o.Status == "cancelled")} cancelados, {orders.Count(o => o.Status != "paid" && o.Status != "cancelled" && o.Status != "invalid")} pendientes de pago.");
        var topSold = valid.SelectMany(o => o.Items).Where(t => !string.IsNullOrWhiteSpace(t))
            .GroupBy(t => t!).OrderByDescending(g => g.Count()).Take(5);
        foreach (var g in topSold)
            sb.AppendLine($"  - Más vendido: {Short(g.Key, 70)} ({g.Count()} pedidos)");

        // Costos
        var settings = await _pricingService.GetOrCreateSettingsAsync(organizationId, cancellationToken);
        sb.AppendLine($"Costos de cruce: tipo de cambio {settings.UsdToMxnRate:0.00}, cruce/impuestos {settings.ImportFeePercent:0.#}%, " +
                      $"comisión ML {settings.DefaultMarketplaceFeePercent:0.#}%, retenciones {settings.PlatformTaxWithholdingPercent:0.#}%, " +
                      $"envío {Money(settings.EstimatedShippingMxn)}, ganancia mínima {Money(settings.MinProfitAmountMxn)}, " +
                      $"publicación automática {(settings.AutoPublishEnabled ? "activada" : "desactivada")}.");

        var accounts = await _context.MarketplaceAccounts
            .CountAsync(x => x.OrganizationId == organizationId && x.Marketplace == "MercadoLibre" && x.Status == "Connected", cancellationToken);
        sb.AppendLine($"Cuentas de Mercado Libre conectadas: {accounts}.");

        return sb.ToString();
    }

    private static string Money(decimal? value) => value.HasValue ? value.Value.ToString("C0", Mx) : "—";

    private static string Short(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "(sin título)";
        value = value.Replace('\n', ' ').Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }
}
