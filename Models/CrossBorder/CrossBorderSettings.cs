using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace MarketplaceSync.Web.Models.CrossBorder;

public class CrossBorderSettings
{
    [Key]
    public int Id { get; set; }

    public Guid OrganizationId { get; set; }

    [ValidateNever]
    public Organization? Organization { get; set; }

    /// <summary>
    /// Tipo de cambio USD a MXN (ej. 20.25).
    /// </summary>
    [Column(TypeName = "numeric(10,4)")]
    public decimal UsdToMxnRate { get; set; } = 20.0000m;

    /// <summary>
    /// Porcentaje estimado de cruce e importación (arancel + DTA + IVA fronterizo o comisión de comercializadora).
    /// Ejemplo: 15.00 para 15%.
    /// </summary>
    [Column(TypeName = "numeric(6,2)")]
    public decimal ImportFeePercent { get; set; } = 15.00m;

    /// <summary>
    /// Cuota fija adicional en USD por paquete (ej. manejo en PO Box El Paso o costo de puente).
    /// </summary>
    [Column(TypeName = "numeric(10,2)")]
    public decimal AdditionalHandlingFeeUsd { get; set; } = 0.00m;

    /// <summary>
    /// Margen neto de ganancia deseado en porcentaje (ej. 25.00 para 25%).
    /// </summary>
    [Column(TypeName = "numeric(6,2)")]
    public decimal TargetProfitMarginPercent { get; set; } = 25.00m;

    /// <summary>
    /// Ganancia neta mínima en pesos mexicanos (ej. $150.00 MXN).
    /// </summary>
    [Column(TypeName = "numeric(10,2)")]
    public decimal MinProfitAmountMxn { get; set; } = 150.00m;

    /// <summary>
    /// Costo de Mercado Envíos asumido por el vendedor en productos >= $299 MXN (ej. $110.00 MXN).
    /// </summary>
    [Column(TypeName = "numeric(10,2)")]
    public decimal EstimatedShippingMxn { get; set; } = 110.00m;

    /// <summary>
    /// Comisión de Mercado Libre predeterminada para autopartes si no se conoce la categoría exacta (ej. 14.50%).
    /// </summary>
    [Column(TypeName = "numeric(6,2)")]
    public decimal DefaultMarketplaceFeePercent { get; set; } = 14.50m;

    /// <summary>
    /// Porcentaje de retención de impuestos de Mercado Libre (1% ISR + 8% IVA = 9% para persona física con RFC).
    /// </summary>
    [Column(TypeName = "numeric(6,2)")]
    public decimal PlatformTaxWithholdingPercent { get; set; } = 9.00m;

    /// <summary>
    /// Si está habilitado, los productos extraídos que cumplan atributos y margen se publican automáticamente.
    /// </summary>
    public bool AutoPublishEnabled { get; set; } = false;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
