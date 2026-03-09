using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;
using Listenarr.Api.Services;
using Listenarr.Api.Services.Search;
using Listenarr.Domain.Models;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;

namespace Listenarr.Api.Controllers;

[ApiController]
[Route("api/v{version:apiVersion}/library/manual-import")]
[Tags("Library")]
public class ManualImportController : ControllerBase
{
    private readonly ILogger<ManualImportController> _logger;
    private readonly IAudiobookRepository _audiobookRepository;
    private readonly IMetadataService _metadataService;
    private readonly IFileNamingService _fileNamingService;
    private readonly IConfigurationService _configService;
    private readonly IScanQueueService _scanQueueService;
    private readonly IRootFolderService _rootFolderService;
    private readonly IAudiobookMetadataService? _audiobookMetadataService;
    private readonly MetadataConverters? _metadataConverters;

    public ManualImportController(
        ILogger<ManualImportController> logger,
        IAudiobookRepository audiobookRepository,
        IMetadataService metadataService,
        IFileNamingService fileNamingService,
        IConfigurationService configService,
        IScanQueueService scanQueueService,
        IRootFolderService rootFolderService,
        IAudiobookMetadataService? audiobookMetadataService = null,
        MetadataConverters? metadataConverters = null)
    {
        _logger = logger;
        _audiobookRepository = audiobookRepository;
        _metadataService = metadataService;
        _fileNamingService = fileNamingService;
        _configService = configService;
        _scanQueueService = scanQueueService;
        _rootFolderService = rootFolderService;
        _audiobookMetadataService = audiobookMetadataService;
        _metadataConverters = metadataConverters;
    }

    /// <summary>
    /// Preview the files available for manual import from a directory.
    /// </summary>
    /// <param name="path">Absolute path to the directory to scan.</param>
    /// <returns>List of files with relative paths, sizes, and tentative metadata.</returns>
    [HttpGet("preview")]
    public ActionResult<object> Preview([FromQuery] string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return BadRequest(new { error = "Path is required" });

            var normalized = Path.GetFullPath(path);
            if (!Directory.Exists(normalized)) return NotFound(new { error = "Directory not found" });

            var files = Directory.EnumerateFiles(normalized, "*.*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .Select(f => new
                {
                    relativePath = Path.GetRelativePath(normalized, f),
                    fullPath = f,
                    size = new FileInfo(f).Length,
                    // Simple heuristics for sample metadata
                    series = (string?)null,
                    season = (string?)null,
                    episodes = (string?)null,
                    quality = (string?)null,
                    languages = new string[] { "English" },
                    releaseType = "Unknown"
                })
                .ToList();

            var items = files.Select(f => new
            {
                relativePath = f.relativePath,
                fullPath = f.fullPath,
                size = FormatSize(f.size),
                series = f.series,
                season = f.season,
                episodes = f.episodes,
                quality = f.quality,
                languages = f.languages,
                releaseType = f.releaseType
            }).ToList();

            return Ok(new { items });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error previewing manual import for path {Path}", path);
            return StatusCode(500, new { error = "Failed to preview import" });
        }
    }

    /// <summary>
    /// Start a manual import operation, copying or moving selected files into the library.
    /// </summary>
    /// <param name="request">Import configuration including source path, mode, input mode (copy/move), and selected file items.</param>
    /// <returns>Summary of imported files with success/failure details per item.</returns>
    [HttpPost]
    public async Task<ActionResult<object>> Start([FromBody] ManualImportRequest request)
    {
        try
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Path))
                return BadRequest(new { error = "Invalid request" });

            var normalized = Path.GetFullPath(request.Path);
            if (!Directory.Exists(normalized))
                return NotFound(new { error = "Directory not found" });

            if (request.Mode == "automatic")
            {
                // TODO: Implement automatic import
                return BadRequest(new { error = "Automatic import not yet implemented" });
            }
            else if (request.Mode == "interactive" && request.Items != null && request.Items.Any())
            {
                var results = new List<ManualImportResult>();
                // Track destination paths used within this batch so we avoid collisions between items
                var usedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                
                // Count files per audiobook to determine if multi-file import
                var filesPerAudiobook = request.Items
                    .GroupBy(i => i.MatchedAudiobookId)
                    .ToDictionary(g => g.Key, g => g.Count());
                
                _logger.LogDebug("Manual import batch: {ItemCount} items, filesPerAudiobook: {AudiobookFileCount}", request.Items.Count, string.Join(";", filesPerAudiobook.Select(x => $"{x.Key}:{x.Value}")));
                
                foreach (var item in request.Items)
                {
                    var isMultiFile = filesPerAudiobook.TryGetValue(item.MatchedAudiobookId, out var count) && count > 1;
                    _logger.LogDebug("Importing item {Index}: {Path} for audiobook {AudiobookId}, isMultiFile: {IsMultiFile}", request.Items.IndexOf(item), item.FullPath, item.MatchedAudiobookId, isMultiFile);
                    var result = await ImportFileAsync(item, request.InputMode ?? "copy", usedDestinations, isMultiFile);
                    _logger.LogDebug("Import result {Index}: Success={Success}, Destination={Destination}, Error={Error}", request.Items.IndexOf(item), result.Success, result.DestinationPath, result.Error);
                    results.Add(result);
                }

                var successCount = results.Count(r => r.Success);
                _logger.LogInformation("Manual import batch completed: {SuccessCount}/{TotalCount} succeeded, usedDestinations: {DestinationCount}", successCount, results.Count, usedDestinations.Count);
                return Ok(new
                {
                    importedCount = successCount,
                    totalCount = results.Count,
                    results = results
                });
            }

            return BadRequest(new { error = "No items to import" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error starting manual import");
            return StatusCode(500, new { error = "Failed to start import" });
        }
    }

    private async Task<ManualImportResult> ImportFileAsync(ManualImportItem item, string inputMode, HashSet<string>? usedDestinations = null, bool isMultiFile = false)
    {
        try
        {
            // Validate FullPath
            if (string.IsNullOrWhiteSpace(item.FullPath))
            {
                return new ManualImportResult
                {
                    Success = false,
                    Error = "FullPath is required",
                    FilePath = item.FullPath
                };
            }

            // Get the associated audiobook
            var audiobook = await _audiobookRepository.GetByIdAsync(item.MatchedAudiobookId);
            if (audiobook == null)
            {
                return new ManualImportResult
                {
                    Success = false,
                    Error = $"Audiobook with ID {item.MatchedAudiobookId} not found",
                    FilePath = item.FullPath
                };
            }

            // Check if source file exists
            if (!System.IO.File.Exists(item.FullPath))
            {
                return new ManualImportResult
                {
                    Success = false,
                    Error = "Source file not found",
                    FilePath = item.FullPath
                };
            }

            // Check if audiobook has a base path
            if (string.IsNullOrWhiteSpace(audiobook.BasePath))
            {
                var appSettings = await _configService.GetApplicationSettingsAsync() ?? new ApplicationSettings();
                var fallbackPath = appSettings.OutputPath;

                if (string.IsNullOrWhiteSpace(fallbackPath))
                {
                    return new ManualImportResult
                    {
                        Success = false,
                        Error = "No base path configured for audiobook and no default output path set",
                        FilePath = item.FullPath
                    };
                }

                // Use fallback path
                audiobook.BasePath = fallbackPath;
            }

            // Extract metadata from the file
            var metadata = await _metadataService.ExtractFileMetadataAsync(item.FullPath);
            if (metadata == null)
            {
                return new ManualImportResult
                {
                    Success = false,
                    Error = "Failed to extract metadata from file",
                    FilePath = item.FullPath
                };
            }

            // Tier 1: if audiobook lacks metadata and an ASIN is available (from DB or embedded tag),
            // fetch from Audimeta and apply — ensures correct naming even for files with empty tags.
            var resolvedAsin = !string.IsNullOrWhiteSpace(audiobook.Asin) ? audiobook.Asin
                : !string.IsNullOrWhiteSpace(metadata?.Asin) ? metadata.Asin
                : null;

            if (resolvedAsin != null && _audiobookMetadataService != null && _metadataConverters != null
                && (string.IsNullOrWhiteSpace(audiobook.Title) || string.IsNullOrWhiteSpace(audiobook.Authors?.FirstOrDefault())))
            {
                try
                {
                    var rawResult = await _audiobookMetadataService.GetMetadataAsync(resolvedAsin, cache: true);
                    if (rawResult is Listenarr.Api.Services.AudimetaBookResponse audimeta)
                    {
                        var converted = _metadataConverters.ConvertAudimetaToMetadata(audimeta, resolvedAsin, "Audimeta");
                        if (!string.IsNullOrWhiteSpace(converted.Title)) audiobook.Title = converted.Title;
                        if (!string.IsNullOrWhiteSpace(converted.Series)) audiobook.Series = converted.Series;
                        if (!string.IsNullOrWhiteSpace(converted.SeriesNumber)) audiobook.SeriesNumber = converted.SeriesNumber;
                        if (!string.IsNullOrWhiteSpace(converted.PublishYear)) audiobook.PublishYear = converted.PublishYear;
                        if (!string.IsNullOrWhiteSpace(converted.Asin)) audiobook.Asin = converted.Asin;
                        var authors = converted.Authors?.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
                        if (authors?.Count > 0) audiobook.Authors = authors;
                        await _audiobookRepository.UpdateAsync(audiobook);
                        _logger.LogInformation("Tier 1 metadata fetch updated audiobook {Id} from ASIN {Asin}", audiobook.Id, resolvedAsin);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Tier 1 metadata fetch failed for audiobook {Id} ASIN {Asin} — import continues", audiobook.Id, resolvedAsin);
                }
            }

            // Generate destination path using appropriate naming pattern
            var destinationPath = await GenerateManualImportPathAsync(audiobook, metadata!, item.FullPath, isMultiFile);

            // Ensure destination directory exists
            var destinationDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            // If source and destination are the same file, skip move/copy (file already in place)
            var sourceNorm = Path.GetFullPath(item.FullPath);
            var destNorm   = Path.GetFullPath(destinationPath);
            var alreadyInPlace = string.Equals(sourceNorm, destNorm, StringComparison.OrdinalIgnoreCase);

            if (!alreadyInPlace)
            {
                // If destination file exists (and is a different file), create a unique filename
                var preUniquePath = destinationPath;
                try
                {
                    _logger.LogDebug("Resolving unique destination for manual import: {Dest}, usedDestinations count: {Count}", destinationPath, usedDestinations?.Count ?? 0);
                    destinationPath = FileUtils.GetUniqueDestinationPath(destinationPath, System.IO.File.Exists, usedDestinations);
                    if (preUniquePath != destinationPath)
                    {
                        _logger.LogDebug("Unique destination changed from {Old} to {New}", preUniquePath, destinationPath);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                    _logger.LogWarning(ex, "Failed to generate unique destination filename for manual import: {Destination}", destinationPath);
                }

                // Move or copy the file
                try
                {
                    _logger.LogDebug("Attempting to {Operation} file from {Source} to {Destination}", inputMode == "move" ? "move" : "copy", item.FullPath, destinationPath);
                    if (inputMode == "move")
                    {
                        System.IO.File.Move(item.FullPath, destinationPath, overwrite: false);
                        _logger.LogInformation("Moved file {Source} to {Destination}", item.FullPath, destinationPath);
                    }
                    else
                    {
                        System.IO.File.Copy(item.FullPath, destinationPath, overwrite: false);
                        _logger.LogInformation("Copied file {Source} to {Destination}", item.FullPath, destinationPath);
                    }
                }
                catch (IOException ex) when (System.IO.File.Exists(destinationPath))
                {
                    _logger.LogWarning(ex, "Destination file already exists despite unique name generation: {Destination}", destinationPath);
                    throw;
                }
            }
            else
            {
                _logger.LogInformation("Source and destination are the same file, skipping move/copy: {Path}", destinationPath);
            }
            // Write ASIN to embedded file tags (non-critical — failure is logged, not thrown)
            if (!string.IsNullOrWhiteSpace(audiobook.Asin))
                await _metadataService.WriteAsinTagAsync(destinationPath, audiobook.Asin);

            // Scope the audiobook's BasePath to the book-level folder so focused scans
            // don't walk the entire series directory and associate sibling files
            var destDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destDir) && audiobook.BasePath != destDir)
            {
                audiobook.BasePath = destDir;
                await _audiobookRepository.UpdateAsync(audiobook);
            }

            // Record the destination to avoid collisions with subsequent items in this batch
            try
            {
                if (usedDestinations != null)
                {
                    usedDestinations.Add(destinationPath);
                    _logger.LogDebug("Added destination to usedDestinations: {Destination}, total count now: {Count}", destinationPath, usedDestinations.Count);
                }
            }
            catch (ArgumentException ex)
            {
                _logger.LogDebug(ex, "Failed tracking used destination during manual import for {Destination}", destinationPath);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogDebug(ex, "Failed tracking used destination during manual import for {Destination}", destinationPath);
            }
            // After a successful move/copy, enqueue a focused scan for the matched audiobook
            try
            {
                if (_scanQueueService != null)
                {
                    var scanJobId = await _scanQueueService.EnqueueScanAsync(audiobook.Id, destinationPath);
                    _logger.LogInformation("Enqueued focused scan {ScanJobId} for audiobook {AudiobookId} (path: {Path}) after manual import", scanJobId, audiobook.Id, destinationPath);
                }
                else
                {
                    _logger.LogDebug("IScanQueueService not available - skipping enqueue of focused scan for audiobook {AudiobookId}", audiobook.Id);
                }
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue scan for audiobook {AudiobookId} after manual import", audiobook.Id);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue scan for audiobook {AudiobookId} after manual import", audiobook.Id);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue scan for audiobook {AudiobookId} after manual import", audiobook.Id);
            }

            return new ManualImportResult
            {
                Success = true,
                FilePath = item.FullPath,
                DestinationPath = destinationPath,
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
            _logger.LogError(ex, "Error importing file {FilePath}", item.FullPath);
            return new ManualImportResult
            {
                Success = false,
                Error = ex.Message,
                FilePath = item.FullPath
            };
        }
    }

    private async Task<string> GenerateManualImportPathAsync(Audiobook audiobook, AudioMetadata metadata, string sourceFilePath, bool isMultiFile = false)
    {
        // Get the configured folder/file naming patterns from settings
        var settings = await _configService.GetApplicationSettingsAsync();
        var folderPattern = settings.FolderNamingPattern;
        var filePattern = isMultiFile ? settings.MultiFileNamingPattern : settings.FileNamingPattern;

        // If a custom BasePath is set (different from configured OutputPath AND not a known
        // root folder), store directly under that path using file-only naming.
        // If BasePath IS a configured root folder, treat it as a library destination and
        // apply the full folder+file naming pattern so files are properly organised.
        var basePath = audiobook.BasePath ?? string.Empty;
        var configuredOutput = settings.OutputPath ?? string.Empty;
        var isCustomBasePath = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(basePath))
            {
                var baseFull = Path.GetFullPath(basePath);
                var configuredFull = string.IsNullOrWhiteSpace(configuredOutput) ? string.Empty : Path.GetFullPath(configuredOutput);
                isCustomBasePath = !string.Equals(baseFull, configuredFull, StringComparison.OrdinalIgnoreCase);

                // Even if it differs from OutputPath, don't treat it as custom when it
                // matches a configured root folder — those are all valid library destinations.
                if (isCustomBasePath)
                {
                    var rootFolders = await _rootFolderService.GetAllAsync();
                    var isRootFolder = rootFolders.Any(r =>
                    {
                        try { return string.Equals(Path.GetFullPath(r.Path), baseFull, StringComparison.OrdinalIgnoreCase); }
                        catch { return false; }
                    });
                    if (isRootFolder) isCustomBasePath = false;
                }
            }
        }
        catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException) {
            isCustomBasePath = !string.IsNullOrWhiteSpace(basePath) && !string.IsNullOrWhiteSpace(configuredOutput)
                && !string.Equals(basePath, configuredOutput, StringComparison.OrdinalIgnoreCase);
        }

        // Get the file extension from the source file (preserve original extension)
        var extension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".m4b"; // Fallback if no extension
        }

        // Build variables for the pattern.
        // Title/Author priority: file embedded tags → audiobook DB → empty string.
        //   The embedded "album" tag is the canonical book title in audiobook conventions (set by
        //   Audible and most ripping tools), so it is more reliable than path-parsed DB data.
        //   DB title still wins when the file has no embedded tags (older files, stripped tags).
        // Series/SeriesNumber/Year priority: DB first (Audimeta is more accurate here),
        //   then file embedded tags as fallback.
        var title = !string.IsNullOrWhiteSpace(metadata.Album) ? metadata.Album
            : !string.IsNullOrWhiteSpace(audiobook.Title) ? audiobook.Title
            : !string.IsNullOrWhiteSpace(metadata.Title) ? metadata.Title
            : string.Empty;

        // Append subtitle from DB when the embedded file tag gives a generic series title
        // (e.g. Album="The Land", Subtitle="Founding" → "The Land: Founding") so each book
        // in a series gets a unique path rather than all colliding into the same folder.
        if (!string.IsNullOrWhiteSpace(audiobook.Subtitle)
            && !title.Contains(audiobook.Subtitle, StringComparison.OrdinalIgnoreCase))
        {
            title = string.IsNullOrWhiteSpace(title) ? audiobook.Subtitle : $"{title}: {audiobook.Subtitle}";
        }

        var author = !string.IsNullOrWhiteSpace(metadata.AlbumArtist) ? metadata.AlbumArtist
            : !string.IsNullOrWhiteSpace(metadata.Artist) ? metadata.Artist
            : audiobook.Authors?.FirstOrDefault() is { Length: > 0 } a ? a
            : string.Empty;

        var series = !string.IsNullOrWhiteSpace(audiobook.Series) ? audiobook.Series
            : !string.IsNullOrWhiteSpace(metadata.Series) ? metadata.Series
            : string.Empty;

        var seriesNumber = !string.IsNullOrWhiteSpace(audiobook.SeriesNumber) ? audiobook.SeriesNumber
            : (metadata.SeriesPosition.HasValue ? metadata.SeriesPosition.Value.ToString("G") : null)
            ?? string.Empty;

        var year = !string.IsNullOrWhiteSpace(audiobook.PublishYear) ? audiobook.PublishYear
            : (metadata.Year.HasValue ? metadata.Year.Value.ToString() : null)
            ?? (metadata.PublishDate.HasValue ? metadata.PublishDate.Value.Year.ToString() : null)
            ?? string.Empty;

        var variables = new Dictionary<string, object>
        {
            { "Author", author },
            { "Title", title },
            { "Series", series },
            { "SeriesNumber", seriesNumber },
            { "Year", year },
            { "DiskNumber", (metadata.DiscNumber.HasValue && metadata.DiscNumber.Value > 0) ? metadata.DiscNumber.Value.ToString("00") : string.Empty },
            { "ChapterNumber", (metadata.TrackNumber.HasValue && metadata.TrackNumber.Value > 0) ? metadata.TrackNumber.Value.ToString() : string.Empty },
        };

        string relativePath;

        if (string.IsNullOrWhiteSpace(folderPattern))
        {
            // Legacy behavior: use FileNamingPattern as the full relative path pattern
            var legacyPattern = string.IsNullOrWhiteSpace(filePattern)
                ? "{Author}/{Title}/{Title}"
                : filePattern;

            relativePath = _fileNamingService.ApplyNamingPattern(legacyPattern, variables, treatAsFilename: false);
        }
        else if (isCustomBasePath)
        {
            // Custom base path: only apply file naming pattern, not folder pattern
            // (the BasePath already represents the folder location)
            var effectiveFilePattern = string.IsNullOrWhiteSpace(filePattern) ? "{Title}" : filePattern;

            var patternAllowsSubfolders = effectiveFilePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || effectiveFilePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || effectiveFilePattern.IndexOf('/') >= 0
                || effectiveFilePattern.IndexOf('\\') >= 0;

            relativePath = _fileNamingService.ApplyNamingPattern(effectiveFilePattern, variables, treatAsFilename: !patternAllowsSubfolders);
        }
        else
        {
            // New behavior: separate folder and file patterns
            var effectiveFilePattern = string.IsNullOrWhiteSpace(filePattern) ? "{Title}" : filePattern;

            var folderRelative = _fileNamingService.ApplyNamingPattern(folderPattern, variables, treatAsFilename: false);

            var patternAllowsSubfolders = effectiveFilePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || effectiveFilePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || effectiveFilePattern.IndexOf('/') >= 0
                || effectiveFilePattern.IndexOf('\\') >= 0;

            var fileRelative = _fileNamingService.ApplyNamingPattern(effectiveFilePattern, variables, treatAsFilename: !patternAllowsSubfolders);

            relativePath = string.IsNullOrWhiteSpace(folderRelative)
                ? fileRelative
                : CombineWithOptionalBase(folderRelative, fileRelative);
        }

        // Ensure it has the correct extension
        if (!relativePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            relativePath += extension;
        }

        return string.IsNullOrWhiteSpace(basePath)
            ? relativePath
            : CombineWithOptionalBase(basePath, relativePath);
    }

    private static string CombineWithOptionalBase(string? basePath, string candidatePath)
    {
        var normalizedPath = candidatePath.Trim();

        if (string.IsNullOrEmpty(normalizedPath))
        {
            return normalizedPath;
        }

        if (Path.IsPathRooted(normalizedPath) || string.IsNullOrWhiteSpace(basePath))
        {
            return normalizedPath;
        }

        var relativePath = normalizedPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.IsPathRooted(relativePath))
        {
            return relativePath;
        }

        var normalizedBasePath = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(normalizedBasePath)
            ? relativePath
            : normalizedBasePath + Path.DirectorySeparatorChar + relativePath;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var units = new[] { "KiB", "MiB", "GiB", "TiB" };
        double size = bytes / 1024.0;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024.0;
            unit++;
        }
        return $"{size:F1} {units[unit]}";
    }
}

public class ManualImportRequest
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "interactive";

    [JsonPropertyName("inputMode")]
    public string? InputMode { get; set; } // "move" or "copy"

    [JsonPropertyName("items")]
    public List<ManualImportItem>? Items { get; set; }
}

public class ManualImportItem
{
    [JsonPropertyName("relativePath")]
    public string RelativePath { get; set; } = string.Empty;

    [JsonPropertyName("fullPath")]
    [Required]
    public string? FullPath { get; set; }

    [JsonPropertyName("matchedAudiobookId")]
    public int MatchedAudiobookId { get; set; }

    [JsonPropertyName("releaseGroup")]
    public string? ReleaseGroup { get; set; }

    [JsonPropertyName("qualityProfileId")]
    public int? QualityProfileId { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("size")]
    public string? Size { get; set; }
}

public class ManualImportResult
{
    public bool Success { get; set; }
    public string? FilePath { get; set; }
    public string? DestinationPath { get; set; }
    public int? AudiobookId { get; set; }
    public string? AudiobookTitle { get; set; }
    public string? Error { get; set; }
}


