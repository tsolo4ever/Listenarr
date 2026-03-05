using System.Collections.Generic;
using System.IO;
using Xunit;
using Moq;
using Listenarr.Api.Services;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Tests
{
    /// <summary>
    /// Tests for ApplyNamingPattern sentinel/cleanup logic when optional variables are empty.
    /// Covers bracket groups, adjacent separators, and path segment removal.
    /// </summary>
    public class FileNamingService_EmptyVariableTests
    {
        private readonly FileNamingService _service;

        public FileNamingService_EmptyVariableTests()
        {
            var configMock = new Mock<IConfigurationService>();
            var loggerMock = new Mock<ILogger<FileNamingService>>();
            _service = new FileNamingService(configMock.Object, loggerMock.Object);
        }

        // --- Bracket group removal ---

        [Fact]
        public void EmptySingleVariableInBrackets_BracketGroupRemoved()
        {
            // [{Series}] with no series → bracket group stripped entirely
            var vars = new Dictionary<string, object>
            {
                { "Author", "Jane Doe" },
                { "Title", "Some Book" },
                { "Series", string.Empty },
            };

            var result = _service.ApplyNamingPattern("{Author}/{Title} [{Series}]", vars);

            Assert.DoesNotContain("[", result);
            Assert.DoesNotContain("]", result);
            Assert.Contains("Some Book", result);
        }

        [Fact]
        public void EmptyTwoVariablesInBrackets_BracketGroupRemoved()
        {
            // [{Series} {SeriesNumber}] with both empty → entire bracket group stripped
            var vars = new Dictionary<string, object>
            {
                { "Author", "Jane Doe" },
                { "Title", "Some Book" },
                { "Series", string.Empty },
                { "SeriesNumber", string.Empty },
            };

            var result = _service.ApplyNamingPattern("{Author}/{Title} [{Series} {SeriesNumber}]", vars);

            Assert.DoesNotContain("[", result);
            Assert.DoesNotContain("]", result);
            Assert.Contains("Jane Doe", result);
            Assert.Contains("Some Book", result);
        }

        [Fact]
        public void PopulatedVariablesInBrackets_BracketGroupPreserved()
        {
            // [{Series} {SeriesNumber}] with both present → bracket group kept
            var vars = new Dictionary<string, object>
            {
                { "Author", "Brandon Sanderson" },
                { "Title", "The Final Empire" },
                { "Series", "Mistborn" },
                { "SeriesNumber", "1" },
            };

            var result = _service.ApplyNamingPattern("{Author}/{Title} [{Series} {SeriesNumber}]", vars);

            Assert.Contains("[Mistborn 1]", result);
        }

        [Fact]
        public void PartiallyPopulatedBrackets_SeriesPresentNumberEmpty_BracketPreserved()
        {
            // [{Series} {SeriesNumber}] — series present, number missing → keep bracket (series name is useful)
            var vars = new Dictionary<string, object>
            {
                { "Author", "Brandon Sanderson" },
                { "Title", "The Final Empire" },
                { "Series", "Mistborn" },
                { "SeriesNumber", string.Empty },
            };

            var result = _service.ApplyNamingPattern("{Author}/{Title} [{Series} {SeriesNumber}]", vars);

            Assert.Contains("Mistborn", result);
        }

        // --- Path segment removal (folder pattern) ---

        [Fact]
        public void EmptySeriesInFolderPattern_SeriesSegmentRemoved()
        {
            // {Author}/{Series}/{Title} with empty series → middle segment dropped
            var vars = new Dictionary<string, object>
            {
                { "Author", "Jane Doe" },
                { "Series", string.Empty },
                { "Title", "Standalone Book" },
            };

            var result = _service.ApplyNamingPattern("{Author}/{Series}/{Title}", vars);

            var parts = result.Split(Path.DirectorySeparatorChar);
            Assert.Equal(2, parts.Length);
            Assert.Equal("Jane Doe", parts[0]);
            Assert.Equal("Standalone Book", parts[1]);
        }

        [Fact]
        public void PopulatedSeriesInFolderPattern_AllSegmentsPresent()
        {
            var vars = new Dictionary<string, object>
            {
                { "Author", "Jane Doe" },
                { "Series", "My Series" },
                { "Title", "Book One" },
            };

            var result = _service.ApplyNamingPattern("{Author}/{Series}/{Title}", vars);

            var parts = result.Split(Path.DirectorySeparatorChar);
            Assert.Equal(3, parts.Length);
            Assert.Equal("Jane Doe", parts[0]);
            Assert.Equal("My Series", parts[1]);
            Assert.Equal("Book One", parts[2]);
        }

        // --- Realistic combined folder+file patterns ---

        [Fact]
        public void FullPattern_NoSeries_ProducesCleanPath()
        {
            // Pattern: {Author}/{Series}/{Year} - {Title} [{Series} {SeriesNumber}]
            // No series/number → clean path with no brackets or empty segments
            var vars = new Dictionary<string, object>
            {
                { "Author", "Jane Doe" },
                { "Series", string.Empty },
                { "SeriesNumber", string.Empty },
                { "Year", "2023" },
                { "Title", "Standalone" },
            };

            var result = _service.ApplyNamingPattern(
                "{Author}/{Series}/{Year} - {Title} [{Series} {SeriesNumber}]", vars);

            Assert.DoesNotContain("[", result);
            Assert.DoesNotContain("]", result);
            Assert.Contains("Jane Doe", result);
            Assert.Contains("2023", result);
            Assert.Contains("Standalone", result);
            // No double separators
            Assert.DoesNotContain("//", result);
            Assert.DoesNotContain("  ", result);
        }

        [Fact]
        public void FullPattern_WithSeries_ProducesCorrectPath()
        {
            var vars = new Dictionary<string, object>
            {
                { "Author", "Brandon Sanderson" },
                { "Series", "Mistborn" },
                { "SeriesNumber", "1" },
                { "Year", "2006" },
                { "Title", "The Final Empire" },
            };

            var result = _service.ApplyNamingPattern(
                "{Author}/{Series}/{Year} - {Title} [{Series} {SeriesNumber}]", vars);

            Assert.Contains("Mistborn", result);
            Assert.Contains("2006", result);
            Assert.Contains("The Final Empire", result);
            Assert.Contains("[Mistborn 1]", result);
        }

        // --- Missing keys (variable not in dict) ---

        [Fact]
        public void MissingVariableKey_TreatedSameAsEmpty_NoOrphanedBrackets()
        {
            // Series key not in dict at all (legacy caller behavior) → same cleanup as empty value
            var vars = new Dictionary<string, object>
            {
                { "Author", "Jane Doe" },
                { "Title", "Some Book" },
                // "Series" and "SeriesNumber" deliberately omitted
            };

            var result = _service.ApplyNamingPattern("{Author}/{Title} [{Series} {SeriesNumber}]", vars);

            Assert.DoesNotContain("[", result);
            Assert.DoesNotContain("]", result);
        }
    }
}
