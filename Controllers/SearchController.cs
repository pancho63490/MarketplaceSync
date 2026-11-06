using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

/// <summary>Búsqueda rápida del buscador Ctrl+K: productos (título, parte, SKU) y pedidos (número, comprador, producto).</summary>
[Authorize]
public sealed class SearchController : Controller
{
    private readonly AppDbContext _context;
    private readonly IOrganizationContext _organizationContext;

    public SearchController(AppDbContext context, IOrganizationContext organizationContext)
    {
        _context = context;
        _organizationContext = organizationContext;
    }

    [HttpGet]
    public async Task<IActionResult> Quick(string? q, CancellationToken cancellationToken)
    {
        q = (q ?? string.Empty).Trim();
        if (q.Length < 2)
            return Json(new { products = Array.Empty<object>(), orders = Array.Empty<object>() });
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var like = "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var mx = System.Globalization.CultureInfo.GetCultureInfo("es-MX");

        var products = await _context.Products.AsNoTracking()
            .Where(p => p.OrganizationId == org && p.Status != "Archived" &&
                        (EF.Functions.ILike(p.Title ?? "", like) || EF.Functions.ILike(p.Model ?? "", like) ||
                         p.Variants.Any(v => EF.Functions.ILike(v.Sku, like)) ||
                         p.MarketplacePublications.Any(m => m.ExternalItemId == q.ToUpper())))
            .OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt)
            .Take(6)
            .Select(p => new
            {
                p.Id, p.Title, p.Model, p.ImageUrl, p.Status,
                Stock = p.Variants.Where(v => v.IsActive).Sum(v => (int?)v.Stock),
                Price = p.MarketplacePublications.Where(m => m.Marketplace == "MercadoLibre").Select(m => m.Price).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var orders = await _context.SalesOrders.AsNoTracking()
            .Where(o => o.OrganizationId == org &&
                        (EF.Functions.ILike(o.ExternalOrderId, like) || EF.Functions.ILike(o.BuyerNickname ?? "", like) ||
                         EF.Functions.ILike(o.ExternalPackId ?? "", like) || EF.Functions.ILike(o.ShippingId ?? "", like) ||
                         o.Items.Any(i => EF.Functions.ILike(i.Title ?? "", like))))
            .OrderByDescending(o => o.DateCreated)
            .Take(6)
            .Select(o => new { o.Id, o.ExternalOrderId, o.BuyerNickname, o.PaidAmount, o.DateCreated, o.Status, Title = o.Items.Select(i => i.Title).FirstOrDefault() })
            .ToListAsync(cancellationToken);

        return Json(new
        {
            products = products.Select(p => new
            {
                label = p.Title ?? "Producto sin título",
                sub = string.Join(" · ", new[]
                {
                    string.IsNullOrWhiteSpace(p.Model) ? null : "Parte " + p.Model,
                    p.Price is > 0 ? p.Price.Value.ToString("C0", mx) : null,
                    p.Stock is { } st ? $"{st} en stock" : null
                }.Where(x => x is not null)),
                image = p.ImageUrl,
                url = Url.Action("Details", "Products", new { id = p.Id })
            }),
            orders = orders.Select(o => new
            {
                label = $"#{o.ExternalOrderId} · {o.BuyerNickname ?? "comprador"}",
                sub = $"{o.Title} · {o.PaidAmount.ToString("C0", mx)} · {o.DateCreated.AddHours(-7):dd/MM}" + (o.Status == "cancelled" ? " · cancelado" : ""),
                url = Url.Action("Details", "Orders", new { id = o.Id })
            })
        });
    }
}
