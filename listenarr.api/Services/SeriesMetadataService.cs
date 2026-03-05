using System.Text.Json;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Api.Services
{
    public interface ISeriesMetadataService
    {
        Task<SeriesMetadata?> GetByAsinAsync(string seriesAsin);
        Task<SeriesMetadata> UpsertAsync(string seriesAsin, string name);
        Task<SeriesMetadata> RefreshFromAudimetaAsync(string seriesAsin, string region = "us", bool forceRefresh = false);
        Task<SeriesMetadata> SetIsCompleteAsync(string seriesAsin, bool isComplete);
    }

    public class SeriesMetadataService : ISeriesMetadataService
    {
        private readonly IDbContextFactory<ListenArrDbContext> _dbFactory;
        private readonly AudimetaService _audimeta;
        private readonly ILogger<SeriesMetadataService> _logger;

        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);
        private static readonly TimeSpan InferCompleteThreshold = TimeSpan.FromDays(365 * 5);

        public SeriesMetadataService(
            IDbContextFactory<ListenArrDbContext> dbFactory,
            AudimetaService audimeta,
            ILogger<SeriesMetadataService> logger)
        {
            _dbFactory = dbFactory;
            _audimeta = audimeta;
            _logger = logger;
        }

        public async Task<SeriesMetadata?> GetByAsinAsync(string seriesAsin)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.SeriesMetadata.FirstOrDefaultAsync(s => s.SeriesAsin == seriesAsin);
        }

        public async Task<SeriesMetadata> UpsertAsync(string seriesAsin, string name)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var existing = await db.SeriesMetadata.FirstOrDefaultAsync(s => s.SeriesAsin == seriesAsin);
            if (existing != null)
            {
                // Update name if it changed
                if (!string.IsNullOrWhiteSpace(name) && existing.Name != name)
                {
                    existing.Name = name;
                    await db.SaveChangesAsync();
                }
                return existing;
            }

            var entry = new SeriesMetadata { SeriesAsin = seriesAsin, Name = name };
            db.SeriesMetadata.Add(entry);
            await db.SaveChangesAsync();
            return entry;
        }

        public async Task<SeriesMetadata> RefreshFromAudimetaAsync(string seriesAsin, string region = "us", bool forceRefresh = false)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var record = await db.SeriesMetadata.FirstOrDefaultAsync(s => s.SeriesAsin == seriesAsin);
            if (record == null)
            {
                record = new SeriesMetadata { SeriesAsin = seriesAsin, Name = seriesAsin };
                db.SeriesMetadata.Add(record);
            }

            // Skip if recently fetched (unless forced)
            if (!forceRefresh && record.LastFetchedAt.HasValue && DateTime.UtcNow - record.LastFetchedAt.Value < RefreshInterval)
                return record;

            // Fetch book list to get total count and newest release date
            var booksRaw = await _audimeta.GetBooksBySeriesAsinAsync(seriesAsin, region);
            if (booksRaw != null)
            {
                try
                {
                    var json = JsonSerializer.Serialize(booksRaw);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // Response may be an array directly or wrapped in { books: [] }
                    JsonElement books = root.ValueKind == JsonValueKind.Array
                        ? root
                        : (root.TryGetProperty("books", out var b) ? b : root);

                    if (books.ValueKind == JsonValueKind.Array)
                    {
                        record.TotalBooks = books.GetArrayLength();

                        // Find newest releaseDate across all books
                        DateTime? newest = null;
                        foreach (var book in books.EnumerateArray())
                        {
                            var dateStr = book.TryGetProperty("releaseDate", out var rd) ? rd.GetString()
                                        : book.TryGetProperty("publishDate", out var pd) ? pd.GetString()
                                        : null;
                            if (dateStr != null && DateTime.TryParse(dateStr, out var d))
                            {
                                if (newest == null || d > newest)
                                    newest = d;
                            }
                        }
                        record.NewestBookDate = newest;
                        record.IsCompleteInferred = newest.HasValue
                            && DateTime.UtcNow - newest.Value > InferCompleteThreshold;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to parse series books response for {SeriesAsin}", seriesAsin);
                }
            }

            // Fetch series-level description
            var seriesRaw = await _audimeta.GetSeriesMetaAsync(seriesAsin, region);
            if (seriesRaw != null)
            {
                try
                {
                    var json = JsonSerializer.Serialize(seriesRaw);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("description", out var desc))
                        record.Description = desc.GetString();
                    if (string.IsNullOrWhiteSpace(record.Name) || record.Name == seriesAsin)
                        if (root.TryGetProperty("name", out var nm))
                            record.Name = nm.GetString() ?? record.Name;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Failed to parse series meta response for {SeriesAsin}", seriesAsin);
                }
            }

            record.LastFetchedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return record;
        }

        public async Task<SeriesMetadata> SetIsCompleteAsync(string seriesAsin, bool isComplete)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var record = await db.SeriesMetadata.FirstOrDefaultAsync(s => s.SeriesAsin == seriesAsin)
                ?? throw new KeyNotFoundException($"Series {seriesAsin} not found");
            record.IsComplete = isComplete;
            await db.SaveChangesAsync();
            return record;
        }
    }
}
