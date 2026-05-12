using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace MarketplaceSync.Web.Controllers
{
    public class EbayController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EbayController> _logger;

        public EbayController(IConfiguration configuration, ILogger<EbayController> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet]
        [Route("Ebay/MarketplaceAccountDeletion")]
        public IActionResult MarketplaceAccountDeletionVerification([FromQuery(Name = "challenge_code")] string challengeCode)
        {
            if (string.IsNullOrWhiteSpace(challengeCode))
            {
                return BadRequest("Missing challenge_code");
            }

            var verificationToken = _configuration["Ebay:MarketplaceDeletionVerificationToken"];

            if (string.IsNullOrWhiteSpace(verificationToken))
            {
                return StatusCode(500, "Missing eBay verification token configuration.");
            }

            var endpoint = "https://marketplacesync.onrender.com/Ebay/MarketplaceAccountDeletion";

            var valueToHash = challengeCode + verificationToken + endpoint;

            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(valueToHash));
            var challengeResponse = Convert.ToHexString(hashBytes).ToLower();

            return Json(new
            {
                challengeResponse
            });
        }

        [HttpPost]
        [Route("Ebay/MarketplaceAccountDeletion")]
        public async Task<IActionResult> MarketplaceAccountDeletionNotification()
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();

            _logger.LogInformation("eBay Marketplace Account Deletion notification received: {Body}", body);

            // Aquí después podemos borrar datos relacionados al usuario de eBay si los guardamos.
            // Por ahora solo confirmamos recepción para que eBay valide el endpoint.

            return Ok();
        }
    }
}