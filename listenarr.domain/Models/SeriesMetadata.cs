namespace Listenarr.Domain.Models
{
    public class SeriesMetadata
    {
        public int Id { get; set; }
        public string SeriesAsin { get; set; } = string.Empty;  // Audible series ASIN (unique)
        public string Name { get; set; } = string.Empty;        // Series name from Audimeta
        public string? Description { get; set; }                // From /series/:asin
        public int? TotalBooks { get; set; }                    // Count from /series/books/:asin (cached)
        public bool IsComplete { get; set; } = false;           // User-managed override
        public bool? IsCompleteInferred { get; set; }           // true if newest book > 5 years old
        public DateTime? NewestBookDate { get; set; }           // Latest releaseDate across books in series
        public DateTime? LastFetchedAt { get; set; }            // When data was last refreshed from Audimeta
    }
}
