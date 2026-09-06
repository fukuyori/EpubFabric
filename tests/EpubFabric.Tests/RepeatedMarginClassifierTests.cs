using EpubFabric.Core.Models;
using EpubFabric.Layout;

namespace EpubFabric.Tests;

public class RepeatedMarginClassifierTests
{
    private readonly RepeatedMarginClassifier _classifier = new();

    [Fact]
    public void Classify_RecurringTopTextWithChangingDigits_IsHeaderAndExcluded()
    {
        var pages = Enumerable.Range(1, 4)
            .Select(pageNumber => Page(pageNumber, Block(
                $"Chapter {pageNumber}  Introduction",
                new BoundingBox(0.1, 0.02, 0.7, 0.025),
                BlockType.Header,
                excluded: true)))
            .ToList();

        _classifier.Classify(pages);

        Assert.All(pages.SelectMany(page => page.Blocks), block =>
        {
            Assert.Equal(BlockType.Header, block.Type);
            Assert.True(block.IsExcluded);
        });
    }

    [Fact]
    public void Classify_UniqueMarginText_IsRestoredToBody()
    {
        var pages = new List<DocumentPage>
        {
            Page(1, Block("最初の短い本文", new BoundingBox(0.1, 0.02, 0.5, 0.025), BlockType.Header, excluded: true)),
            Page(2, Block("別の短い本文", new BoundingBox(0.1, 0.02, 0.5, 0.025), BlockType.Header, excluded: true)),
            Page(3, Block("末尾の短い本文", new BoundingBox(0.1, 0.95, 0.5, 0.025), BlockType.Footer, excluded: true)),
        };

        _classifier.Classify(pages);

        Assert.All(pages.SelectMany(page => page.Blocks), block =>
        {
            Assert.Equal(BlockType.Body, block.Type);
            Assert.False(block.IsExcluded);
        });
    }

    [Theory]
    [InlineData("12")]
    [InlineData("p. 12")]
    [InlineData("Page 12")]
    [InlineData("—12—")]
    [InlineData("12 / 324")]
    public void Classify_PageNumberPatterns_AreExcluded(string text)
    {
        var page = Page(1, Block(
            text,
            new BoundingBox(0.45, 0.95, 0.1, 0.025),
            BlockType.Footer,
            excluded: true));

        _classifier.Classify([page]);

        var block = Assert.Single(page.Blocks);
        Assert.Equal(BlockType.PageNumber, block.Type);
        Assert.True(block.IsExcluded);
    }

    [Fact]
    public void Classify_ManuallyEditedMarginBlock_IsNotChanged()
    {
        var block = Block(
            "固有のヘッダー",
            new BoundingBox(0.1, 0.02, 0.5, 0.025),
            BlockType.Header,
            excluded: true);
        block.IsManuallyEdited = true;

        _classifier.Classify([Page(1, block)]);

        Assert.Equal(BlockType.Header, block.Type);
        Assert.True(block.IsExcluded);
    }

    private static DocumentPage Page(int pageNumber, params PageBlock[] blocks)
    {
        var page = new DocumentPage
        {
            PageNumber = pageNumber,
            OriginalImagePath = $"page-{pageNumber}.png",
            ProcessedImagePath = $"page-{pageNumber}.png",
            PreviewImagePath = $"page-{pageNumber}.png",
            Width = 1000,
            Height = 1400,
        };
        page.Blocks.AddRange(blocks);
        return page;
    }

    private static PageBlock Block(string text, BoundingBox bounds, BlockType type, bool excluded) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        PageNumber = 1,
        Bounds = bounds,
        Type = type,
        OcrText = text,
        OcrConfidence = 0.95,
        ReadingOrder = 0,
        IsExcluded = excluded,
    };
}
