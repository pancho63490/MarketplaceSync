using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MarketplaceSync.Web.Helpers;

/// <summary>
/// Íconos de línea (Lucide, licencia ISC) desde el sprite local <c>/img/icons.svg</c>.
/// Uso en vistas: <c>@Html.Icon("package")</c> o <c>@Html.Icon("refresh-cw", "icon-sm")</c>.
/// Son decorativos (aria-hidden): el texto del botón o enlace es lo que se lee.
/// </summary>
public static class IconHtmlHelper
{
    private const string SpritePath = "/img/icons.svg?v=4";

    public static IHtmlContent Icon(this IHtmlHelper html, string name, string? cssClass = null)
    {
        var safeName = HtmlEncoder.Default.Encode(name);
        var classes = string.IsNullOrWhiteSpace(cssClass) ? "icon" : "icon " + HtmlEncoder.Default.Encode(cssClass);
        return new HtmlString(
            $"<svg class=\"{classes}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"{SpritePath}#i-{safeName}\"></use></svg>");
    }
}
