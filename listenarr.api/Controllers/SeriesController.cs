using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v1/series")]
    public class SeriesController : ControllerBase
    {
        private readonly ISeriesMetadataService _seriesMetadata;
        private readonly ILogger<SeriesController> _logger;

        public SeriesController(ISeriesMetadataService seriesMetadata, ILogger<SeriesController> logger)
        {
            _seriesMetadata = seriesMetadata;
            _logger = logger;
        }

        [HttpGet("{asin}")]
        public async Task<IActionResult> GetSeries(string asin)
        {
            var record = await _seriesMetadata.GetByAsinAsync(asin);
            if (record == null)
                return NotFound();
            return Ok(MapToDto(record));
        }

        [HttpPatch("{asin}")]
        public async Task<IActionResult> SetIsComplete(string asin, [FromBody] SetIsCompleteRequest request)
        {
            try
            {
                var record = await _seriesMetadata.SetIsCompleteAsync(asin, request.IsComplete);
                return Ok(MapToDto(record));
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
        }

        [HttpPost("{asin}/refresh")]
        public async Task<IActionResult> Refresh(string asin, [FromQuery] string region = "us")
        {
            var record = await _seriesMetadata.GetByAsinAsync(asin);
            if (record == null)
                return NotFound();

            // Force refresh by clearing LastFetchedAt before calling refresh
            var refreshed = await _seriesMetadata.RefreshFromAudimetaAsync(asin, region, forceRefresh: true);
            return Ok(MapToDto(refreshed));
        }

        private static object MapToDto(SeriesMetadata s) => new
        {
            seriesAsin = s.SeriesAsin,
            name = s.Name,
            description = s.Description,
            totalBooks = s.TotalBooks,
            isComplete = s.IsComplete,
            isCompleteInferred = s.IsCompleteInferred,
            newestBookDate = s.NewestBookDate,
            lastFetchedAt = s.LastFetchedAt
        };
    }

    public class SetIsCompleteRequest
    {
        public bool IsComplete { get; set; }
    }
}
