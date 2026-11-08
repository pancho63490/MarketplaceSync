using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MarketplaceSync.Web.Models.Invoicing;

/// <summary>Datos fiscales del vendedor para timbrar con Facturama (API Multiemisor). El CSD no se guarda aquí: va directo a Facturama.</summary>
public sealed class InvoicingSettings
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>RFC del vendedor (emisor).</summary>
    [MaxLength(13)]
    public string? IssuerRfc { get; set; }

    /// <summary>Nombre o razón social tal como aparece en la Constancia de Situación Fiscal (sin régimen de capital).</summary>
    [MaxLength(300)]
    public string? IssuerName { get; set; }

    /// <summary>Régimen fiscal del emisor (626 RESICO, 612 Actividad empresarial, 625 Plataformas tecnológicas…).</summary>
    [MaxLength(3)]
    public string? FiscalRegime { get; set; }

    /// <summary>Código postal del domicilio fiscal (lugar de expedición).</summary>
    [MaxLength(5)]
    public string? ExpeditionZip { get; set; }

    [MaxLength(10)]
    public string Serie { get; set; } = "ML";

    public int NextFolio { get; set; } = 1;

    [MaxLength(10)]
    public string GlobalSerie { get; set; } = "GL";

    public int NextGlobalFolio { get; set; } = 1;

    /// <summary>Clave de producto SAT por omisión (8 dígitos) para productos sin clave propia.</summary>
    [MaxLength(8)]
    public string? DefaultProductCode { get; set; }

    /// <summary>Tasa de IVA incluida en los precios (0.16, u 0.08 si aplica el estímulo de la región fronteriza).</summary>
    [Column(TypeName = "numeric(5,4)")]
    public decimal IvaRate { get; set; } = 0.16m;

    /// <summary>Forma de pago SAT (31 Intermediario pagos, 04 Tarjeta de crédito…).</summary>
    [MaxLength(2)]
    public string PaymentForm { get; set; } = "31";

    /// <summary>Subir el XML y PDF a la compra en Mercado Libre al timbrar (el comprador la ve ahí).</summary>
    public bool UploadToMercadoLibre { get; set; } = true;

    /// <summary>Avisar por Telegram cuando un comprador pide factura y recordar la factura global.</summary>
    public bool NotifyTelegram { get; set; } = true;

    public DateTime? CsdUploadedAt { get; set; }

    [MaxLength(200)]
    public string? CsdStatus { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public bool IsReady =>
        !string.IsNullOrWhiteSpace(IssuerRfc) && !string.IsNullOrWhiteSpace(IssuerName) &&
        !string.IsNullOrWhiteSpace(FiscalRegime) && !string.IsNullOrWhiteSpace(ExpeditionZip) &&
        !string.IsNullOrWhiteSpace(DefaultProductCode) && CsdUploadedAt.HasValue;
}

/// <summary>Un CFDI timbrado: individual (a un comprador) o global (público en general, ventas del mes sin factura).</summary>
public sealed class SalesInvoice
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>individual o global.</summary>
    [MaxLength(10)]
    public string Kind { get; set; } = InvoiceKinds.Individual;

    /// <summary>Id del CFDI en Facturama (para descargar o cancelar).</summary>
    [MaxLength(80)]
    public string? ProviderId { get; set; }

    /// <summary>Folio fiscal (UUID del SAT).</summary>
    [MaxLength(40)]
    public string? Uuid { get; set; }

    [MaxLength(10)]
    public string Serie { get; set; } = string.Empty;

    public int Folio { get; set; }

    [MaxLength(13)]
    public string ReceiverRfc { get; set; } = string.Empty;

    [MaxLength(300)]
    public string ReceiverName { get; set; } = string.Empty;

    [MaxLength(4)]
    public string CfdiUse { get; set; } = string.Empty;

    [Column(TypeName = "numeric(12,2)")]
    public decimal Subtotal { get; set; }

    [Column(TypeName = "numeric(12,2)")]
    public decimal Tax { get; set; }

    [Column(TypeName = "numeric(12,2)")]
    public decimal Total { get; set; }

    /// <summary>issued o cancelled.</summary>
    [MaxLength(12)]
    public string Status { get; set; } = InvoiceStatus.Issued;

    /// <summary>Pack (o pedido) de Mercado Libre al que se subió la factura.</summary>
    [MaxLength(40)]
    public string? MercadoLibrePackId { get; set; }

    public int? MarketplaceAccountId { get; set; }
    public DateTime? UploadedToMercadoLibreAt { get; set; }

    [MaxLength(300)]
    public string? UploadError { get; set; }

    /// <summary>Factura global: año y mes que ampara.</summary>
    public int? PeriodYear { get; set; }
    public int? PeriodMonth { get; set; }

    /// <summary>Número de ventas que ampara.</summary>
    public int OrdersCount { get; set; }

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CancelledAt { get; set; }

    [MaxLength(2)]
    public string? CancelMotive { get; set; }
}

public static class InvoiceKinds
{
    public const string Individual = "individual";
    public const string Global = "global";
}

public static class InvoiceStatus
{
    public const string Issued = "issued";
    public const string Cancelled = "cancelled";
}

/// <summary>Catálogos del SAT que se usan en la app (los más comunes).</summary>
public static class SatCatalogs
{
    public static readonly IReadOnlyList<(string Code, string Label)> FiscalRegimes = new[]
    {
        ("601", "General de Ley Personas Morales"),
        ("603", "Personas Morales con Fines no Lucrativos"),
        ("605", "Sueldos y Salarios"),
        ("606", "Arrendamiento"),
        ("608", "Demás ingresos"),
        ("612", "Personas Físicas con Actividades Empresariales y Profesionales"),
        ("616", "Sin obligaciones fiscales"),
        ("621", "Incorporación Fiscal"),
        ("625", "Actividades Empresariales con ingresos a través de Plataformas Tecnológicas"),
        ("626", "Régimen Simplificado de Confianza (RESICO)")
    };

    public static readonly IReadOnlyList<(string Code, string Label)> CfdiUses = new[]
    {
        ("G01", "Adquisición de mercancías"),
        ("G03", "Gastos en general"),
        ("I04", "Equipo de cómputo y accesorios"),
        ("I08", "Otra maquinaria y equipo"),
        ("S01", "Sin efectos fiscales"),
        ("CP01", "Pagos")
    };

    public static readonly IReadOnlyList<(string Code, string Label)> PaymentForms = new[]
    {
        ("31", "31 · Intermediario pagos (Mercado Pago)"),
        ("04", "04 · Tarjeta de crédito"),
        ("28", "28 · Tarjeta de débito"),
        ("03", "03 · Transferencia"),
        ("01", "01 · Efectivo"),
        ("99", "99 · Por definir")
    };

    public static string RegimeLabel(string? code) =>
        FiscalRegimes.FirstOrDefault(r => r.Code == code).Label is { } label ? $"{code} · {label}" : code ?? "—";

    public static string UseLabel(string? code) =>
        CfdiUses.FirstOrDefault(r => r.Code == code).Label is { } label ? $"{code} · {label}" : code ?? "—";

    public const string PublicRfc = "XAXX010101000";
    public const string ForeignRfc = "XEXX010101000";

    public static bool LooksLikeRfc(string? value) =>
        value is not null && System.Text.RegularExpressions.Regex.IsMatch(value.Trim().ToUpperInvariant(), "^[A-ZÑ&]{3,4}[0-9]{6}[A-Z0-9]{3}$");
}
