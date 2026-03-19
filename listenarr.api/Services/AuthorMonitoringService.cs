/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2025 Robbie Davis
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Background service that periodically checks for new releases by monitored authors and series.
    ///
    /// Two discovery modes (selected by settings):
    ///   RSS mode  — poll a user-configured RSS feed URL, extract Audible ASINs from product links,
    ///               fetch full metadata from Audimeta, match against monitored authors/series.
    ///   Audimeta  — (default when RSS URL is blank) poll /author/books/{asin} per monitored author
    ///               and /series/books/{asin} per monitored series.
    ///
    /// Monitoring is implicit: any author/series where at least one library audiobook is Monitored=true.
    /// New books are added as Wanted (Monitored=true, no file) to trigger AutomaticSearchService.
    /// </summary>
    public class AuthorMonitoringService : BackgroundService
    {
        private readonly ILogger<AuthorMonitoringService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly HttpClient _httpClient;

        // Audible product URL ASIN extraction: /pd/{slug}/{ASIN}
        private static readonly Regex AsinPathRegex =
            new(@"/pd/[^/?]+/([A-Z0-9]{10})", RegexOptions.Compiled);
        private static readonly Regex AsinQueryRegex =
            new(@"[?&]asin=([A-Z0-9]{10})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly JsonSerializerOptions JsonOpts =
            new() { PropertyNameCaseInsensitive = true };

        public AuthorMonitoringService(
            ILogger<AuthorMonitoringService> logger,
            IServiceScopeFactory scopeFactory,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _httpClient = httpClientFactory.CreateClient();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("AuthorMonitoringService started");

            // Wait for app to be fully ready before first cycle
            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = TimeSpan.FromHours(24);
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                    var settings = await configService.GetApplicationSettingsAsync();

                    interval = TimeSpan.FromHours(Math.Max(1, settings.AuthorMonitoringIntervalHours));

                    if (!settings.AuthorMonitoringEnabled)
                    {
                        _logger.LogDebug("AuthorMonitoringService: monitoring disabled, skipping cycle");
                    }
                    else if (!string.IsNullOrWhiteSpace(settings.AuthorMonitoringRssFeedUrl))
                    {
                        await PollRssFeedAsync(settings.AuthorMonitoringRssFeedUrl, stoppingToken);
                    }
                    else
                    {
                        await PollAuthorsAsync(stoppingToken);
                        await PollSeriesAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "AuthorMonitoringService: error during monitoring cycle");
                }

                try { await Task.Delay(interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }

            _logger.LogInformation("AuthorMonitoringService stopped");
        }

        // ─── RSS Mode ─────────────────────────────────────────────────────────────

        private async Task PollRssFeedAsync(string feedUrl, CancellationToken ct)
        {
            _logger.LogInformation("AuthorMonitoringService: polling RSS feed {Url}", feedUrl);
            try
            {
                var xml = await _httpClient.GetStringAsync(feedUrl, ct);
                var doc = XDocument.Parse(xml);
                var atomNs = XNamespace.Get("http://www.w3.org/2005/Atom");

                var links = new List<string>();
                // RSS 2.0: <item><link>URL</link></item>
                foreach (var item in doc.Descendants("item"))
                {
                    var link = item.Element("link")?.Value
                               ?? item.Element(atomNs + "link")?.Attribute("href")?.Value;
                    if (!string.IsNullOrWhiteSpace(link)) links.Add(link);
                }
                // Atom: <entry><link href="URL"/></entry>
                foreach (var entry in doc.Descendants(atomNs + "entry"))
                {
                    var link = entry.Element(atomNs + "link")?.Attribute("href")?.Value;
                    if (!string.IsNullOrWhiteSpace(link)) links.Add(link);
                }

                var asins = links
                    .Select(ExtractAsinFromUrl)
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                _logger.LogInformation("AuthorMonitoringService: RSS feed has {ItemCount} items, {AsinCount} Audible ASINs",
                    links.Count, asins.Count);

                if (asins.Count == 0) return;

                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                var audimeta = scope.ServiceProvider.GetRequiredService<AudimetaService>();

                var allBooks = await repo.GetAllAsync();
                var libraryAsins = BuildLibraryAsinSet(allBooks);
                var (monitoredAuthorAsins, monitoredAuthorNames, monitoredSeriesAsins) =
                    BuildMonitoredSets(allBooks);

                foreach (var asin in asins)
                {
                    if (ct.IsCancellationRequested) break;
                    if (libraryAsins.Contains(asin!)) continue;

                    try
                    {
                        var meta = await audimeta.GetBookMetadataAsync(asin!, useCache: true);
                        if (meta == null) continue;
                        if (IsPerformance(meta.ContentType)) continue;

                        bool matchesAuthor = meta.Authors?.Any(a =>
                            (!string.IsNullOrWhiteSpace(a.Asin) && monitoredAuthorAsins.Contains(a.Asin)) ||
                            (!string.IsNullOrWhiteSpace(a.Name) && monitoredAuthorNames.Contains(a.Name))) ?? false;

                        bool matchesSeries = meta.Series?.Any(s =>
                            !string.IsNullOrWhiteSpace(s.Asin) && monitoredSeriesAsins.Contains(s.Asin)) ?? false;

                        if (!matchesAuthor && !matchesSeries) continue;

                        await AddBookToLibraryAsync(BuildAudiobookFromResponse(meta), repo, "RSS feed");
                        libraryAsins.Add(asin!);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "AuthorMonitoringService: error processing ASIN {Asin} from RSS", asin);
                    }

                    try { await Task.Delay(500, ct); } catch (OperationCanceledException) { throw; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "AuthorMonitoringService: error polling RSS feed {Url}", feedUrl);
            }
        }

        // ─── Audimeta Polling Mode ────────────────────────────────────────────────

        private async Task PollAuthorsAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var audimeta = scope.ServiceProvider.GetRequiredService<AudimetaService>();

            var allBooks = await repo.GetAllAsync();
            var libraryAsins = BuildLibraryAsinSet(allBooks);

            var authorAsins = allBooks
                .Where(b => b.Monitored && b.AuthorAsins != null)
                .SelectMany(b => b.AuthorAsins!)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (authorAsins.Count == 0)
            {
                _logger.LogInformation("AuthorMonitoringService: no monitored author ASINs in library, skipping author poll");
                return;
            }

            _logger.LogInformation("AuthorMonitoringService: polling {Count} author(s) via Audimeta", authorAsins.Count);

            foreach (var authorAsin in authorAsins)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var response = await audimeta.GetBooksByAuthorAsinAsync(authorAsin, page: 1, limit: 50);
                    if (response?.Results == null) continue;

                    foreach (var book in response.Results)
                    {
                        if (string.IsNullOrWhiteSpace(book.Asin)) continue;
                        if (libraryAsins.Contains(book.Asin)) continue;
                        if (IsPerformance(book.ContentType)) continue;

                        await AddBookToLibraryAsync(BuildAudiobookFromSearchResult(book), repo, $"author ASIN {authorAsin}");
                        libraryAsins.Add(book.Asin);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "AuthorMonitoringService: error polling author ASIN {AuthorAsin}", authorAsin);
                }

                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { throw; }
            }
        }

        private async Task PollSeriesAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var audimeta = scope.ServiceProvider.GetRequiredService<AudimetaService>();

            var allBooks = await repo.GetAllAsync();
            var libraryAsins = BuildLibraryAsinSet(allBooks);

            var seriesAsins = allBooks
                .Where(b => b.Monitored && !string.IsNullOrWhiteSpace(b.SeriesAsin))
                .Select(b => b.SeriesAsin!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (seriesAsins.Count == 0)
            {
                _logger.LogDebug("AuthorMonitoringService: no monitored series ASINs in library, skipping series poll");
                return;
            }

            _logger.LogInformation("AuthorMonitoringService: polling {Count} series via Audimeta", seriesAsins.Count);

            foreach (var seriesAsin in seriesAsins)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var booksObj = await audimeta.GetBooksBySeriesAsinAsync(seriesAsin);
                    if (booksObj == null) continue;

                    // Response may be List<AudimetaSearchResult> or AudimetaSearchResponse envelope
                    List<AudimetaSearchResult>? books = null;
                    var json = JsonSerializer.Serialize(booksObj);
                    try { books = JsonSerializer.Deserialize<List<AudimetaSearchResult>>(json, JsonOpts); }
                    catch { /* try envelope */ }
                    if (books == null)
                    {
                        try { books = JsonSerializer.Deserialize<AudimetaSearchResponse>(json, JsonOpts)?.Results; }
                        catch { /* ignore */ }
                    }
                    if (books == null) continue;

                    foreach (var book in books)
                    {
                        if (string.IsNullOrWhiteSpace(book.Asin)) continue;
                        if (libraryAsins.Contains(book.Asin)) continue;
                        if (IsPerformance(book.ContentType)) continue;

                        await AddBookToLibraryAsync(BuildAudiobookFromSearchResult(book), repo, $"series ASIN {seriesAsin}");
                        libraryAsins.Add(book.Asin);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "AuthorMonitoringService: error polling series ASIN {SeriesAsin}", seriesAsin);
                }

                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { throw; }
            }
        }

        // ─── Shared Helpers ──────────────────────────────────────────────────────

        private async Task AddBookToLibraryAsync(Audiobook audiobook, IAudiobookRepository repo, string source)
        {
            try
            {
                await repo.AddAsync(audiobook);
                _logger.LogInformation(
                    "AuthorMonitoringService: new release added as Wanted — \"{Title}\" (ASIN: {Asin}) discovered via {Source}",
                    audiobook.Title, audiobook.Asin, source);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "AuthorMonitoringService: failed to add book \"{Title}\" (ASIN: {Asin})",
                    audiobook.Title, audiobook.Asin);
            }
        }

        private static Audiobook BuildAudiobookFromResponse(AudimetaBookResponse meta)
        {
            var series = meta.Series?.FirstOrDefault();
            var publishYear = (meta.PublishDate ?? meta.ReleaseDate)?.Length >= 4
                ? (meta.PublishDate ?? meta.ReleaseDate)![..4]
                : null;

            return new Audiobook
            {
                Title = meta.Title,
                Subtitle = meta.Subtitle,
                Asin = meta.Asin,
                Authors = meta.Authors?.Select(a => a.Name ?? string.Empty).Where(n => n.Length > 0).ToList(),
                AuthorAsins = meta.Authors?.Select(a => a.Asin ?? string.Empty).Where(a => a.Length > 0).ToList(),
                Narrators = meta.Narrators?.Select(n => n.Name ?? string.Empty).Where(n => n.Length > 0).ToList(),
                Series = series?.Name,
                SeriesNumber = series?.Position,
                SeriesAsin = series?.Asin,
                Description = meta.Description,
                Publisher = meta.Publisher,
                Language = meta.Language,
                Runtime = meta.LengthMinutes.HasValue ? meta.LengthMinutes.Value * 60 : null,
                PublishYear = publishYear,
                PublishedDate = meta.PublishDate ?? meta.ReleaseDate,
                ImageUrl = meta.ImageUrl,
                Genres = meta.Genres?.Select(g => g.Name ?? string.Empty).Where(n => n.Length > 0).ToList(),
                Explicit = meta.Explicit ?? false,
                Monitored = true,
            };
        }

        private static Audiobook BuildAudiobookFromSearchResult(AudimetaSearchResult result)
        {
            var series = result.Series?.FirstOrDefault();
            var lengthMinutes = result.LengthMinutes ?? result.RuntimeLengthMin ?? result.RuntimeMinutes;
            var publishYear = result.ReleaseDate?.Length >= 4 ? result.ReleaseDate[..4] : null;

            return new Audiobook
            {
                Title = result.Title,
                Subtitle = result.Subtitle,
                Asin = result.Asin,
                Authors = result.Authors?.Select(a => a.Name ?? string.Empty).Where(n => n.Length > 0).ToList(),
                AuthorAsins = result.Authors?.Select(a => a.Asin ?? string.Empty).Where(a => a.Length > 0).ToList(),
                Narrators = result.Narrators?.Select(n => n.Name ?? string.Empty).Where(n => n.Length > 0).ToList(),
                Series = series?.Name,
                SeriesNumber = series?.Position,
                SeriesAsin = series?.Asin,
                Publisher = result.Publisher,
                Language = result.Language,
                Runtime = lengthMinutes.HasValue ? lengthMinutes.Value * 60 : null,
                PublishYear = publishYear,
                PublishedDate = result.ReleaseDate,
                ImageUrl = result.ImageUrl,
                Genres = result.Genres?.Select(g => g.Name ?? string.Empty).Where(n => n.Length > 0).ToList(),
                Monitored = true,
            };
        }

        private static HashSet<string> BuildLibraryAsinSet(List<Audiobook> books) =>
            books.Select(b => b.Asin)
                 .Where(a => !string.IsNullOrWhiteSpace(a))
                 .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        private static (HashSet<string> authorAsins, HashSet<string> authorNames, HashSet<string> seriesAsins)
            BuildMonitoredSets(List<Audiobook> books)
        {
            var authorAsins = books
                .Where(b => b.Monitored && b.AuthorAsins != null)
                .SelectMany(b => b.AuthorAsins!)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var authorNames = books
                .Where(b => b.Monitored && b.Authors != null)
                .SelectMany(b => b.Authors!)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var seriesAsins = books
                .Where(b => b.Monitored && !string.IsNullOrWhiteSpace(b.SeriesAsin))
                .Select(b => b.SeriesAsin!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return (authorAsins, authorNames, seriesAsins);
        }

        private static bool IsPerformance(string? contentType) =>
            string.Equals(contentType, "Performance", StringComparison.OrdinalIgnoreCase);

        private static string? ExtractAsinFromUrl(string url)
        {
            var m = AsinPathRegex.Match(url);
            if (m.Success) return m.Groups[1].Value;
            m = AsinQueryRegex.Match(url);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
