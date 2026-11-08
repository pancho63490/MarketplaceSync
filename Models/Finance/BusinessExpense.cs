using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MarketplaceSync.Web.Models.Finance;

/// <summary>
/// Gasto del negocio que no viene de Mercado Libre: cruce, gasolina, empaques, servicios (Render, IA)…
/// Con <see cref="IsMonthly"/> cuenta cada mes desde <see cref="Date"/> hasta <see cref="EndedOn"/>.
/// </summary>
public sealed class BusinessExpense
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>Ver <see cref="ExpenseCategories"/>.</summary>
    [MaxLength(20)]
    public string Category { get; set; } = ExpenseCategories.Other;

    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Monto en la moneda en que se pagó.</summary>
    [Column(TypeName = "numeric(12,2)")]
    public decimal Amount { get; set; }

    [MaxLength(3)]
    public string Currency { get; set; } = "MXN";

    /// <summary>Monto en pesos (si se pagó en dólares, con el tipo de cambio del día en que se anotó).</summary>
    [Column(TypeName = "numeric(12,2)")]
    public decimal AmountMxn { get; set; }

    public bool IsMonthly { get; set; }
    public DateOnly? EndedOn { get; set; }

    /// <summary>web o telegram.</summary>
    [MaxLength(10)]
    public string Source { get; set; } = "web";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public static class ExpenseCategories
{
    public const string Crossing = "cruce";
    public const string Fuel = "gasolina";
    public const string Packaging = "empaque";
    public const string Shipping = "envios";
    public const string Software = "servicios";
    public const string Ads = "publicidad";
    public const string Tools = "herramientas";
    public const string Other = "otro";

    public static readonly (string Id, string Label, string Hint)[] All =
    {
        (Crossing, "Cruce y aduana", "agente aduanal, comercializadora, puente"),
        (Fuel, "Gasolina y transporte", "viajes a El Paso, estacionamiento"),
        (Packaging, "Empaques", "cajas, cinta, burbuja, etiquetas"),
        (Shipping, "Envíos por fuera", "paqueterías que no son de Mercado Libre"),
        (Software, "Servicios y apps", "Render, IA, dominio, teléfono"),
        (Ads, "Publicidad", "Facebook, volantes… (Mercado Ads se suma solo)"),
        (Tools, "Herramientas y equipo", "impresora térmica, báscula"),
        (Other, "Otro", "")
    };

    public static string Label(string? id) => All.FirstOrDefault(c => c.Id == id).Label ?? "Otro";

    /// <summary>Adivina la categoría por palabras ("gasolina", "cajas", "render"…) para lo que llega por Telegram.</summary>
    public static string Guess(string? text)
    {
        var t = (text ?? string.Empty).ToLowerInvariant();
        if (t.Contains("cruce") || t.Contains("aduan") || t.Contains("agente") || t.Contains("puente") || t.Contains("comercializ")) return Crossing;
        if (t.Contains("gasolin") || t.Contains("gas ") || t.EndsWith("gas") || t.Contains("estacionam") || t.Contains("uber")) return Fuel;
        if (t.Contains("caja") || t.Contains("cinta") || t.Contains("empaq") || t.Contains("burbuja") || t.Contains("bolsa")) return Packaging;
        if (t.Contains("dhl") || t.Contains("fedex") || t.Contains("estafeta") || t.Contains("paqueter") || t.Contains("envío") || t.Contains("envio")) return Shipping;
        if (t.Contains("render") || t.Contains("claude") || t.Contains("anthropic") || t.Contains("dominio") || t.Contains("internet") || t.Contains("telcel") || t.Contains("app")) return Software;
        if (t.Contains("ads") || t.Contains("publicidad") || t.Contains("anuncio")) return Ads;
        if (t.Contains("impresora") || t.Contains("báscula") || t.Contains("bascula") || t.Contains("herramient")) return Tools;
        return Other;
    }
}
