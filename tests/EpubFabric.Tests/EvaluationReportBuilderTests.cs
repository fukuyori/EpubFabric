using System.Text.Json;
using EpubFabric.Core.Models;
using EpubFabric.Evaluation;

namespace EpubFabric.Tests;

public class EvaluationReportBuilderTests
{
    [Fact]
    public void Write_ProducesIndexHtmlAndMetricsJson()
    {
        var pageEvaluation = new PageEvaluation(
            PageNumber: 1,
            BlockCount: 2,
            BlockCountsByType: new Dictionary<string, int> { [nameof(BlockType.Body)] = 2 },
            TextCharsTotal: 10,
            TextCharsIncluded: 8,
            TextCharsExcluded: 0,
            TextCharsDropped: 2,
            FigureCount: 0,
            FigureWithImageCount: 0,
            HeadingCount: 0,
            LowConfidenceIncludedCount: 1,
            TextCoverage: 0.8);

        var summary = new EvaluationSummary(
            PageCount: 1,
            PagesWithBlocks: 1,
            TotalBlocks: 2,
            TextCharsTotal: 10,
            TextCharsIncluded: 8,
            TextCharsExcluded: 0,
            TextCharsDropped: 2,
            FigureCount: 0,
            FigureWithImageCount: 0,
            HeadingCount: 0,
            LowConfidenceIncludedCount: 1,
            TextCoverage: 0.8,
            FigureImageRate: 1.0,
            Pages: [pageEvaluation]);

        var entry = new PageReportEntry(pageEvaluation, "pages/page-0001.jpg", "<p>本文</p>");
        var reportDirectory = Path.Combine(Path.GetTempPath(), $"epubfabric-report-test-{Guid.NewGuid():N}");

        try
        {
            new EvaluationReportBuilder().Write(reportDirectory, "テスト<書籍>", summary, [entry]);

            var indexHtml = File.ReadAllText(Path.Combine(reportDirectory, "index.html"));
            Assert.Contains("ページ 1", indexHtml);
            Assert.Contains("pages/page-0001.jpg", indexHtml);
            Assert.Contains("<p>本文</p>", indexHtml);
            Assert.Contains("テスト&lt;書籍&gt;", indexHtml);

            using var metrics = JsonDocument.Parse(File.ReadAllText(Path.Combine(reportDirectory, "metrics.json")));
            Assert.Equal(1, metrics.RootElement.GetProperty("PageCount").GetInt32());
            Assert.Equal(0.8, metrics.RootElement.GetProperty("TextCoverage").GetDouble());
        }
        finally
        {
            Directory.Delete(reportDirectory, recursive: true);
        }
    }
}
