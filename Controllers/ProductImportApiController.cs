using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers
{
    [ApiController]
    [Route("api/import")]
    public class ProductImportApiController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;

        public ProductImportApiController(AppDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        [HttpPost("product")]
        public async Task<IActionResult> ImportProduct(
            [FromBody] ImportProductRequest request,
            [FromHeader(Name = "X-Importer-Key")] string? importerKey)
        {
            var expectedKey = _configuration["Importer:Key"];

            if (string.IsNullOrWhiteSpace(expectedKey))
            {
                return StatusCode(500, new
                {
                    message = "Falta configurar Importer:Key en appsettings o Render."
                });
            }

            if (string.IsNullOrWhiteSpace(importerKey) || importerKey != expectedKey)
            {
                return Unauthorized(new
                {
                    message = "Importer key inválida."
                });
            }

            if (request == null)
            {
                return BadRequest(new
                {
                    message = "Request vacío."
                });
            }

            if (string.IsNullOrWhiteSpace(request.SourceUrl))
            {
                return BadRequest(new
                {
                    message = "SourceUrl es obligatorio."
                });
            }

            if (string.IsNullOrWhiteSpace(request.SourceMarketplace))
            {
                request.SourceMarketplace = "Amazon";
            }

            var sourceUrl = request.SourceUrl.Trim();
            var sourceProductId = request.SourceProductId?.Trim();

            var existingProduct = await _context.Products
                .FirstOrDefaultAsync(x =>
                    x.SourceUrl == sourceUrl ||
                    (!string.IsNullOrWhiteSpace(sourceProductId) &&
                     x.SourceProductId == sourceProductId &&
                     x.SourceMarketplace == request.SourceMarketplace));

            if (existingProduct != null)
            {
                existingProduct.Title = request.Title ?? existingProduct.Title;
                existingProduct.Description = request.Description ?? existingProduct.Description;
                existingProduct.Brand = request.Brand ?? existingProduct.Brand;
                existingProduct.Model = request.Model ?? existingProduct.Model;
                existingProduct.ImageUrl = request.ImageUrl ?? existingProduct.ImageUrl;

                existingProduct.SourcePrice = request.SourcePrice ?? existingProduct.SourcePrice;
                existingProduct.SourceCurrency = request.SourceCurrency ?? existingProduct.SourceCurrency;
                existingProduct.SourceStock = request.SourceStock ?? existingProduct.SourceStock;
                existingProduct.SourceStatus = request.SourceStatus ?? existingProduct.SourceStatus;
                existingProduct.SourceAvailabilityText = request.SourceAvailabilityText ?? existingProduct.SourceAvailabilityText;

                existingProduct.LastSourceCheckAt = DateTime.UtcNow;
                existingProduct.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                return Ok(new
                {
                    message = "Producto actualizado correctamente.",
                    productId = existingProduct.Id,
                    action = "updated"
                });
            }

            var product = new Product
            {
                SourceMarketplace = request.SourceMarketplace ?? "Amazon",
                SourceUrl = sourceUrl,
                SourceProductId = sourceProductId,

                Title = string.IsNullOrWhiteSpace(request.Title)
                    ? "Producto importado"
                    : request.Title,

                Description = request.Description,
                Brand = request.Brand,
                Model = request.Model,
                ImageUrl = request.ImageUrl,

                SourcePrice = request.SourcePrice,
                SourceCurrency = request.SourceCurrency ?? "USD",
                SourceStock = request.SourceStock,
                SourceStatus = request.SourceStatus ?? "Unknown",
                SourceAvailabilityText = request.SourceAvailabilityText,

                Status = "Draft",

                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                LastSourceCheckAt = DateTime.UtcNow
            };

            _context.Products.Add(product);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = "Producto importado correctamente.",
                productId = product.Id,
                action = "created"
            });
        }
    }

    public class ImportProductRequest
    {
        public string? SourceMarketplace { get; set; }
        public string? SourceUrl { get; set; }
        public string? SourceProductId { get; set; }

        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Brand { get; set; }
        public string? Model { get; set; }
        public string? ImageUrl { get; set; }

        public decimal? SourcePrice { get; set; }
        public string? SourceCurrency { get; set; }
        public int? SourceStock { get; set; }
        public string? SourceStatus { get; set; }
        public string? SourceAvailabilityText { get; set; }
    }
}