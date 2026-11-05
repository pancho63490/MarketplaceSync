using MarketplaceSync.Services.Notifications;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MarketplaceSync.Web.Controllers;

/// <summary>Avisos de la tienda por Telegram.</summary>
[Authorize]
public sealed class NotificationsController : Controller
{
    private readonly IOrganizationContext _organizationContext;
    private readonly TelegramNotificationService _telegram;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        IOrganizationContext organizationContext,
        TelegramNotificationService telegram,
        ILogger<NotificationsController> logger)
    {
        _organizationContext = organizationContext;
        _telegram = telegram;
        _logger = logger;
    }

    private string PublicBaseUrl => $"{Request.Scheme}://{Request.Host}{Request.PathBase}";

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        return View(await _telegram.GetStatusAsync(organizationId, cancellationToken));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConnectBot(string? token, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            var username = await _telegram.ConnectBotAsync(organizationId, token, PublicBaseUrl, cancellationToken);
            TempData["Success"] = $"Bot @{username} conectado. Ahora ábrelo en Telegram y pulsa Iniciar.";
        }
        catch (ArgumentException exception)
        {
            TempData["Error"] = exception.Message;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Telegram connect failed.");
            TempData["Error"] = "No se pudo hablar con Telegram. Intenta de nuevo en un momento.";
        }
        return RedirectToAction(nameof(Index));
    }

    /// <summary>"Ya pulsé Iniciar": lee el bot al momento en vez de esperar al siguiente minuto.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckLink(CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            await _telegram.PollOrganizationAsync(organizationId, cancellationToken);
            var status = await _telegram.GetStatusAsync(organizationId, cancellationToken);
            if (status.ChatLinked)
                TempData["Success"] = "Telegram vinculado. Revisa tu chat: ya te mandé un mensaje.";
            else
                TempData["Warning"] = "Todavía no veo tu mensaje. Abre el bot con el botón de abajo y pulsa Iniciar (o escribe /start).";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Telegram link check failed.");
            TempData["Error"] = "No se pudo leer el bot. Intenta de nuevo en un momento.";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePreferences(bool notifyQuestions, bool notifyOrders, bool notifyStock, bool dailySummary, int dailySummaryHour, CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        await _telegram.SavePreferencesAsync(organizationId, notifyQuestions, notifyOrders, notifyStock, dailySummary, dailySummaryHour, PublicBaseUrl, cancellationToken);
        TempData["Success"] = "Avisos guardados.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendTest(CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        try
        {
            await _telegram.SendTestAsync(organizationId, cancellationToken);
            TempData["Success"] = "Mensaje de prueba enviado. Revisa Telegram.";
        }
        catch (InvalidOperationException exception)
        {
            TempData["Warning"] = exception.Message;
        }
        catch (TelegramApiException exception)
        {
            TempData["Error"] = exception.IsUnauthorizedOrBlocked
                ? "Telegram no dejó enviar el mensaje. Si bloqueaste el bot, desbloquéalo y pulsa Iniciar."
                : "Telegram rechazó el mensaje: " + exception.Message;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Telegram test failed.");
            TempData["Error"] = "No se pudo hablar con Telegram. Intenta de nuevo en un momento.";
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        var organizationId = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        await _telegram.DisconnectAsync(organizationId, cancellationToken);
        TempData["Success"] = "Telegram desconectado. Ya no recibirás avisos.";
        return RedirectToAction(nameof(Index));
    }
}
