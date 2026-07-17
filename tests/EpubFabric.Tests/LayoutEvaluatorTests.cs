using EpubFabric.Core.Models;
using EpubFabric.Evaluation;

namespace EpubFabric.Tests;

public class LayoutEvaluatorTests
{
    [Fact]
    public void Evaluate_ComputesCoverageFiguresAndHeadings()
    {
        var page = new DocumentPage
        {
            PageNumber = 1,
            OriginalImagePath = "page-0001.png",
            ProcessedImagePath = "page-0001.png",
            PreviewImagePath = "page-0001.png",
            Width = 595,
            Height = 842,
            WritingMode = WritingMode.Horizontal,
            Status = PageProcessingStatus.OcrCompleted,
        };

        page.Blocks.AddRange(
        [
            new PageBlock
            {
                Id = "b1", PageNumber = 1, Bounds = new BoundingBox(0, 0, 1, 0.1),
                Type = BlockType.SectionHeading, OcrText = "見出しです", OcrConfidence = 0.95, ReadingOrder = 0,
            },
            new PageBlock
            {
                Id = "b2", PageNumber = 1, Bounds = new BoundingBox(0, 0.1, 1, 0.4),
                Type = BlockType.Body, OcrText = "本文テキスト12345", OcrConfidence = 0.7, ReadingOrder = 1,
            },
            new PageBlock
            {
                Id = "b3", PageNumber = 1, Bounds = new BoundingBox(0, 0.5, 1, 0.3),
                Type = BlockType.Figure, OcrText = "図中の文字", OcrConfidence = 0.9, ReadingOrder = 2,
                ExtractedImagePath = "fig.png",
            },
            new PageBlock
            {
                Id = "b4", PageNumber = 1, Bounds = new BoundingBox(0, 0.8, 1, 0.1),
                Type = BlockType.Figure, OcrText = "", OcrConfidence = 0.9, ReadingOrder = 3,
            },
            new PageBlock
            {
                Id = "b5", PageNumber = 1, Bounds = new BoundingBox(0, 0.95, 1, 0.05),
                Type = BlockType.PageNumber, OcrText = "42", OcrConfidence = 0.99, ReadingOrder = 4,
                IsExcluded = true,
            },
        ]);

        var summary = new LayoutEvaluator().Evaluate([page]);

        Assert.Equal(1, summary.PageCount);
        Assert.Equal(1, summary.PagesWithBlocks);
        Assert.Equal(5, summary.TotalBlocks);

        // 見出し5字 + 本文11字 が採用、図中5字と柱2字は意図的除外、欠落なし。
        Assert.Equal(16, summary.TextCharsIncluded);
        Assert.Equal(7, summary.TextCharsExcluded);
        Assert.Equal(0, summary.TextCharsDropped);
        Assert.Equal(1.0, summary.TextCoverage);

        Assert.Equal(2, summary.FigureCount);
        Assert.Equal(1, summary.FigureWithImageCount);
        Assert.Equal(0.5, summary.FigureImageRate);

        Assert.Equal(1, summary.HeadingCount);

        // OcrConfidence 0.7 の本文1件がしきい値0.85未満で混入扱い。
        Assert.Equal(1, summary.LowConfidenceIncludedCount);

        var pageEval = summary.Pages[0];
        Assert.Equal(2, pageEval.BlockCountsByType[nameof(BlockType.Figure)]);
        Assert.Equal(1, pageEval.BlockCountsByType[nameof(BlockType.Body)]);
    }

    [Fact]
    public void Evaluate_EmptyPageCountsAsUnanalyzed()
    {
        var page = new DocumentPage
        {
            PageNumber = 1,
            OriginalImagePath = "page-0001.png",
            ProcessedImagePath = "page-0001.png",
            PreviewImagePath = "page-0001.png",
            Width = 595,
            Height = 842,
            WritingMode = WritingMode.Horizontal,
            Status = PageProcessingStatus.Error,
        };

        var summary = new LayoutEvaluator().Evaluate([page]);

        Assert.Equal(0, summary.PagesWithBlocks);
        Assert.Equal(1.0, summary.TextCoverage);
        Assert.Equal(1.0, summary.FigureImageRate);
    }
}
