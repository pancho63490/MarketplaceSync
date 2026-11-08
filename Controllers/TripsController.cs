using System.Globalization;
using MarketplaceSync.Services.Purchasing;
using MarketplaceSync.Web.Data;
using MarketplaceSync.Web.Models.Purchasing;
using MarketplaceSync.Web.Services.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers;

/// <summary>Viajes a El Paso (costo real por pieza) y paquetes que llegan a la dirección de El Paso.</summary>
[Authorize]
public sealed class TripsController : Controller
{
    private readonly IOrganizationContext _organizationContext;
    private readonly SupplyTripService _trips;
    private readonly PackageTrackingService _packages;
    private readonly AppDbContext _context;

    public TripsController(IOrganizationContext organizationContext, SupplyTripService trips, PackageTrackingService packages, AppDbContext context)
    {
        _organizationContext = organizationContext;
        _trips = trips;
        _packages = packages;
        _context = context;
    }

    public sealed class TripsIndexViewModel
    {
        public List<(SupplyTrip Trip, TripBreakdown Breakdown, int Packages, int Delivered)> Trips { get; init; } = new();
        public List<InboundPackage> Packages { get; init; } = new();
        public bool TrackingConfigured { get; init; }
        public Dictionary<int, string> TripNames { get; init; } = new();
    }

    public sealed class TripDetailsViewModel
    {
        public required TripBreakdown Breakdown { get; init; }
        public List<InboundPackage> Packages { get; init; } = new();
        public List<string> VariantOptions { get; init; } = new();
        public bool TrackingConfigured { get; init; }
        public bool HasApprovedList { get; init; }
    }

    public sealed class ItemEditRow
    {
        public int Id { get; set; }
        public decimal? UnitCostUsd { get; set; }
        public decimal? ShippingUsd { get; set; }
        public int? Quantity { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var trips = await _context.SupplyTrips.Include(t => t.Items)
            .Where(t => t.OrganizationId == org)
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.Id)
            .Take(30)
            .ToListAsync(cancellationToken);
        var packages = await _context.InboundPackages.AsNoTracking()
            .Where(p => p.OrganizationId == org && p.ArchivedAt == null)
            .OrderBy(p => p.Status == "Delivered").ThenByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        var rows = new List<(SupplyTrip, TripBreakdown, int, int)>();
        foreach (var trip in trips)
        {
            var mine = packages.Where(p => p.SupplyTripId == trip.Id).ToList();
            rows.Add((trip, await _trips.BreakdownAsync(org, trip, cancellationToken), mine.Count, mine.Count(p => p.Status == "Delivered")));
        }
        return View(new TripsIndexViewModel
        {
            Trips = rows,
            Packages = packages,
            TrackingConfigured = _packages.IsConfigured,
            TripNames = trips.ToDictionary(t => t.Id, t => t.Name)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string? name, DateOnly? date, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var trip = await _trips.CreateAsync(org, name, date, cancellationToken);
        return RedirectToAction(nameof(Details), new { id = trip.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var trip = await _trips.LoadAsync(org, id, cancellationToken);
        if (trip is null) return NotFound();
        var variants = await _context.ProductVariants.AsNoTracking()
            .Where(v => v.OrganizationId == org && v.IsActive)
            .OrderBy(v => v.Sku)
            .Select(v => v.Sku + " · " + v.Product.Title)
            .Take(2000)
            .ToListAsync(cancellationToken);
        return View(new TripDetailsViewModel
        {
            Breakdown = await _trips.BreakdownAsync(org, trip, cancellationToken),
            Packages = await _context.InboundPackages.AsNoTracking()
                .Where(p => p.OrganizationId == org && p.SupplyTripId == id && p.ArchivedAt == null)
                .OrderByDescending(p => p.CreatedAt).ToListAsync(cancellationToken),
            VariantOptions = variants,
            TrackingConfigured = _packages.IsConfigured,
            HasApprovedList = await _context.PurchaseLists.AnyAsync(l => l.OrganizationId == org && l.Status == "approved", cancellationToken)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(int id, string? name, DateOnly? date, string? exchangeRate, string? fuel, string? crossing,
        string? taxes, string? other, string? notes, List<ItemEditRow>? items, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var edits = (items ?? new()).ToDictionary(i => i.Id, i => (i.UnitCostUsd ?? 0, i.ShippingUsd ?? 0, i.Quantity ?? 0));
        var (ok, message) = await _trips.UpdateCostsAsync(org, id, name, date, Money(exchangeRate), Money(fuel), Money(crossing), Money(taxes), Money(other), notes, edits, cancellationToken);
        TempData[ok ? "Success" : "Error"] = message;
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddItem(int id, string? product, int quantity, string? unitCostUsd, string? shippingUsd, string? store, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (ok, message) = await _trips.AddItemAsync(org, id, product, quantity, Money(unitCostUsd), Money(shippingUsd), store, cancellationToken);
        TempData[ok ? "Success" : "Error"] = message;
        return RedirectToAction(nameof(Details), null, new { id }, "piezas");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveItem(int id, int itemId, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        if (!await _trips.RemoveItemAsync(org, id, itemId, cancellationToken))
            TempData["Error"] = "No se pudo quitar (el viaje ya se recibió).";
        return RedirectToAction(nameof(Details), null, new { id }, "piezas");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportList(int id, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (ok, message) = await _trips.ImportPurchaseListAsync(org, id, cancellationToken);
        TempData[ok ? "Success" : "Warning"] = message;
        return RedirectToAction(nameof(Details), null, new { id }, "piezas");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Receive(int id, bool addToInventory, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (ok, message) = await _trips.ReceiveAsync(org, id, addToInventory, cancellationToken);
        TempData[ok ? "Success" : "Error"] = message;
        return RedirectToAction(nameof(Details), new { id });
    }

    // ── Paquetes ──

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPackage(string? trackingNumber, string? description, int? tripId, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var (package, message) = await _packages.AddAsync(org, trackingNumber, description, tripId, cancellationToken);
        TempData[package is null ? "Error" : "Success"] = message;
        return tripId is { } t ? RedirectToAction(nameof(Details), null, new { id = t }, "paquetes") : RedirectToAction(nameof(Index), null, null, "paquetes");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Package(int packageId, string? action, int? tripId, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var ok = action == "archive"
            ? await _packages.ArchiveAsync(org, packageId, cancellationToken)
            : await _packages.MarkDeliveredAsync(org, packageId, cancellationToken);
        if (!ok) TempData["Error"] = "No encontré ese paquete.";
        return tripId is { } t ? RedirectToAction(nameof(Details), null, new { id = t }, "paquetes") : RedirectToAction(nameof(Index), null, null, "paquetes");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshPackages(int? tripId, CancellationToken cancellationToken)
    {
        var org = await _organizationContext.RequireCurrentOrganizationIdAsync(cancellationToken);
        var changes = await _packages.RefreshAsync(org, null, cancellationToken);
        TempData["Success"] = changes.Count == 0 ? "Revisé los paquetes: sin cambios." : $"Revisé los paquetes: {changes.Count} con estado nuevo.";
        return tripId is { } t ? RedirectToAction(nameof(Details), null, new { id = t }, "paquetes") : RedirectToAction(nameof(Index), null, null, "paquetes");
    }

    private static decimal Money(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var clean = text.Replace("$", string.Empty).Replace(" ", string.Empty).Trim();
        if (clean.Contains(',') && !clean.Contains('.') && clean.Length - clean.LastIndexOf(',') - 1 is 1 or 2)
            clean = clean.Replace(',', '.');
        clean = clean.Replace(",", string.Empty);
        return decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
