using MarketplaceSync.Web.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceSync.Web.Controllers
{
    public class AdminController : Controller
    {
        private readonly AppDbContext _context;
        private readonly IConfiguration _configuration;

        public AdminController(AppDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        [HttpGet]
        public IActionResult RunMigrations(string key)
        {
            var expectedKey = _configuration["Admin:MigrationKey"];

            if (string.IsNullOrWhiteSpace(expectedKey) || key != expectedKey)
            {
                return Unauthorized("Invalid migration key.");
            }

            try
            {
                _context.Database.Migrate();
                return Ok("Migrations applied successfully.");
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.ToString());
            }
        }
    }
}