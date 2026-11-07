namespace MarketplaceSync.Web.Models;

public class ErrorViewModel
{
    public string? RequestId { get; set; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    /// <summary>Pantalla donde falló (solo se muestra a usuarios con sesión).</summary>
    public string? Path { get; set; }

    /// <summary>Tipo y mensaje del error (solo a usuarios con sesión; sin la traza completa).</summary>
    public string? Detail { get; set; }

    /// <summary>Dónde ocurrió: primeras líneas de la traza que son código de la app.</summary>
    public string? Where { get; set; }
}
