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
