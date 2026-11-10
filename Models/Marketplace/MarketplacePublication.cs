using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MarketplaceSync.Web.Models.Marketplace
{
    public class MarketplacePublication
    {
        [Key]
        public int Id { get; set; }

        // =====================================
        // RELATIONSHIPS
        // =====================================

        public int ProductId { get; set; }

        [ForeignKey(nameof(ProductId))]
        public required Product Product { get; set; }

        public int? MarketplaceAccountId { get; set; }
        public MarketplaceAccount? MarketplaceAccount { get; set; }

        // =====================================
        // MARKETPLACE
        // =====================================

        [MaxLength(50)]
        public string Marketplace { get; set; } = "MercadoLibre";

        // =====================================
        // EXTERNAL
        // =====================================

        [MaxLength(200)]
        public string? ExternalItemId { get; set; }

        [MaxLength(1000)]
        public string? Permalink { get; set; }

        // =====================================
        // PUBLICATION DATA
        // =====================================

        [Column(TypeName = "numeric(18,2)")]
        public decimal? Price { get; set; }

        public int? Stock { get; set; }

        [MaxLength(20)]
        public string? CurrencyId { get; set; }

        [MaxLength(100)]
        public string? CategoryId { get; set; }

        [MaxLength(50)]
        public string? ListingTypeId { get; set; }

        [MaxLength(50)]
        public string? Condition { get; set; }

        // =====================================
        // STATUS
        // =====================================

        [MaxLength(100)]
        public string? Status { get; set; }

        public bool IsPublished { get; set; }

        /// <summary>
        /// Atributos elegidos para Mercado Libre (por la IA o por el usuario), como JSON:
        /// [{"Id":"BRAND","ValueId":null,"ValueName":"ACDelco"}].
        /// </summary>
        public string? AttributesJson { get; set; }

        /// <summary>Unidades vendidas según Mercado Libre (sold_quantity).</summary>
        public int? SoldQuantity { get; set; }

        /// <summary>Comisión real de Mercado Libre para el precio actual (listing_prices).</summary>
        public decimal? SaleFeeMxn { get; set; }

        /// <summary>Precio con el que se calculó <see cref="SaleFeeMxn"/>; si cambia, se recalcula.</summary>
        public decimal? SaleFeeForPrice { get; set; }

        /// <summary>Última vez que se leyó de Mercado Libre.</summary>
        public DateTime? LastSyncedAt { get; set; }

        /// <summary>Último stock enviado a Mercado Libre desde el inventario de la app.</summary>
        public DateTime? LastStockPushAt { get; set; }

        [MaxLength(500)]
        public string? LastStockPushError { get; set; }

        /// <summary>Resumen del último paso del piloto automático (qué falta para publicar).</summary>
        [MaxLength(1000)]
        public string? AutopilotNotes { get; set; }

        // =====================================
        // PROMOCIÓN ACTIVA
        // =====================================

        /// <summary>Promoción en la que está hoy (PRICE_DISCOUNT, DEAL, SELLER_CAMPAIGN…); null = sin promoción.</summary>
        [MaxLength(30)]
        public string? PromotionType { get; set; }

        /// <summary>Id de la campaña (P-MLM…, C-MLM…); null en descuentos individuales.</summary>
        [MaxLength(60)]
        public string? PromotionId { get; set; }

        /// <summary>Id de la oferta en campañas que lo piden para salir (VOLUME, SMART, co-fondeadas).</summary>
        [MaxLength(120)]
        public string? PromotionOfferId { get; set; }

        /// <summary>started (activa) o pending (programada).</summary>
        [MaxLength(20)]
        public string? PromotionStatus { get; set; }

        /// <summary>Precio con la promoción (lo que paga el comprador).</summary>
        public decimal? PromotionPrice { get; set; }

        /// <summary>Precio sin promoción.</summary>
        public decimal? PromotionOriginalPrice { get; set; }

        /// <summary>% que pone Mercado Libre en campañas co-fondeadas (null = lo pone todo el vendedor).</summary>
        public decimal? PromotionMeliPercent { get; set; }

        public DateTime? PromotionEndsAt { get; set; }
        public DateTime? PromotionCheckedAt { get; set; }

        // =====================================
        // CATÁLOGO (competencia por el botón de compra)
        // =====================================

        /// <summary>La publicación compite en una ficha de catálogo de Mercado Libre.</summary>
        public bool CatalogListing { get; set; }

        [MaxLength(40)]
        public string? CatalogProductId { get; set; }

        /// <summary>winning, sharing_first_place, competing (perdiendo) o listed.</summary>
        [MaxLength(30)]
        public string? CatalogStatus { get; set; }

        /// <summary>Precio con el que ganarías (de /items/{id}/price_to_win).</summary>
        [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(12,2)")]
        public decimal? CatalogPriceToWin { get; set; }

        [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(12,2)")]
        public decimal? CatalogWinnerPrice { get; set; }

        /// <summary>Motivos por los que no compite (separados por coma, en el código de Mercado Libre).</summary>
        [MaxLength(400)]
        public string? CatalogReasons { get; set; }

        /// <summary>Ventajas que tiene el ganador y tú no (envío gratis, Full, meses…), separadas por coma.</summary>
        [MaxLength(300)]
        public string? CatalogOpportunities { get; set; }

        public DateTime? CatalogCheckedAt { get; set; }

        // =====================================
        // CALIDAD DE LA PUBLICACIÓN (/item/{id}/performance)
        // =====================================

        /// <summary>0 a 100.</summary>
        public int? QualityScore { get; set; }

        /// <summary>Basic, Standard o Professional.</summary>
        [MaxLength(20)]
        public string? QualityLevel { get; set; }

        /// <summary>Lo que falta (JSON: título, botón, link y si baja la calificación).</summary>
        [MaxLength(4000)]
        public string? QualityActionsJson { get; set; }

        public DateTime? QualityCheckedAt { get; set; }

        // =====================================
        // PRECIO AUTOMÁTICO DE MERCADO LIBRE (/pricing-automation)
        // =====================================

        /// <summary>ACTIVE o PAUSED; null = sin precio automático.</summary>
        [MaxLength(20)]
        public string? AutoPriceStatus { get; set; }

        /// <summary>INT (lo más bajo en Mercado Libre) o INT_EXT (también fuera de Mercado Libre).</summary>
        [MaxLength(20)]
        public string? AutoPriceRule { get; set; }

        /// <summary>Precio mínimo que puede poner Mercado Libre (la app lo mantiene arriba de tu ganancia mínima).</summary>
        [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(12,2)")]
        public decimal? AutoPriceMin { get; set; }

        [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(12,2)")]
        public decimal? AutoPriceMax { get; set; }

        /// <summary>Por qué está en pausa (ITEM_NO_ACTIVE, PROMO, COMPETITORS).</summary>
        [MaxLength(40)]
        public string? AutoPriceCause { get; set; }

        public DateTime? AutoPriceCheckedAt { get; set; }

        // =====================================
        // OPINIONES Y PRECIO POR CANTIDAD
        // =====================================

        /// <summary>Promedio de estrellas de las opiniones (1 a 5).</summary>
        [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "numeric(3,2)")]
        public decimal? RatingAverage { get; set; }

        public int? RatingCount { get; set; }

        public DateTime? ReviewsCheckedAt { get; set; }

        /// <summary>Tramos de precio por cantidad en Mercado Libre (JSON: id, piezas mínimas, precio c/u).</summary>
        [MaxLength(1000)]
        public string? QuantityPricesJson { get; set; }

        public DateTime? QuantityPricesCheckedAt { get; set; }

        // =====================================
        // DATES
        // =====================================

        public DateTime? PublishedAt { get; set; }

        public DateTime CreatedAt { get; set; } =
            DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } =
            DateTime.UtcNow;

        public ICollection<MarketplacePublicationVariant> Variants { get; set; }
            = new List<MarketplacePublicationVariant>();
    }
}
