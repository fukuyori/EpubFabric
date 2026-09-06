using EpubFabric.Core.Models;
using EpubFabric.Layout;

namespace EpubFabric.Tests;

public class SourceTableOfContentsClassifierTests
{
    [Fact]
    public void Classify_ExcludesPrintedTocUntilNextProsePage()
    {
        var first = Page(1,
            Block("title", BlockType.ChapterTitle, "Book title", 0),
            Block("toc", BlockType.Subheading, "Table of Contents", 1),
            Block("entry-1", BlockType.ChapterTitle, "1. First Chapter", 2));
        var continuation = Page(2,
            Block("entry-2", BlockType.ChapterTitle, "2. Second Chapter", 0),
            Block("question-entry", BlockType.Body, "What are the issues in using open-source software and where should I look?", 1));
        var prose = Page(3,
            Block("preface", BlockType.SectionHeading, "Preface", 0),
            Block("body", BlockType.Body, "This is a sufficiently long prose paragraph that begins the actual book content.", 1));

        var changed = new SourceTableOfContentsClassifier().Classify([first, continuation, prose]);

        Assert.Equal(4, changed);
        Assert.False(first.Blocks[0].IsExcluded);
        Assert.True(first.Blocks[1].IsExcluded);
        Assert.True(first.Blocks[2].IsExcluded);
        Assert.All(continuation.Blocks, block => Assert.True(block.IsExcluded));
        Assert.All(prose.Blocks, block => Assert.False(block.IsExcluded));
    }

    [Fact]
    public void Classify_LocalTocStopsAtProseOnSamePage()
    {
        var page = Page(1,
            Block("chapter", BlockType.ChapterTitle, "Chapter 1. Philosophy", 0),
            Block("toc", BlockType.Subheading, "Table of Contents", 1),
            Block("entry", BlockType.Body, "Culture? What culture?", 2),
            Block("quote", BlockType.Body, "Those who do not understand Unix are condemned to reinvent it, poorly.", 3));

        var changed = new SourceTableOfContentsClassifier().Classify([page]);

        Assert.Equal(2, changed);
        Assert.False(page.Blocks[0].IsExcluded);
        Assert.True(page.Blocks[1].IsExcluded);
        Assert.True(page.Blocks[2].IsExcluded);
        Assert.False(page.Blocks[3].IsExcluded);
    }

    [Fact]
    public void Classify_IndentedLongTocEntryDoesNotEndToc()
    {
        var page = Page(1,
            Block("part", BlockType.Subheading, "Context", 0),
            Block("toc", BlockType.Subheading, "Table of Contents", 1),
            Block("entry", BlockType.Body, "At play in the groves of academe and the open-source movement onward.", 2, x: 0.16));

        var changed = new SourceTableOfContentsClassifier().Classify([page]);

        Assert.Equal(3, changed);
        Assert.All(page.Blocks, block => Assert.True(block.IsExcluded));
    }

    [Fact]
    public void Classify_ExcludesUnmarkedJapaneseMagazineContentsPage()
    {
        var page = Page(5,
            Block("heading-1", BlockType.SectionHeading, "解説", 0),
            Block("entry-1", BlockType.Subheading, "最初の記事", 1),
            Block("page-1", BlockType.Body, "12", 2),
            Block("entry-2", BlockType.Subheading, "二番目の記事", 3),
            Block("page-2", BlockType.Body, "24", 4),
            Block("entry-3", BlockType.SectionHeading, "三番目の記事", 5),
            Block("page-3", BlockType.Body, "36", 6),
            Block("entry-4", BlockType.Body, "四番目の記事", 7),
            Block("page-4", BlockType.Body, "48", 8),
            Block("author-1", BlockType.Body, "著者一", 9),
            Block("author-2", BlockType.Body, "著者二", 10),
            Block("footer", BlockType.Body, "次号予告", 11));

        var changed = new SourceTableOfContentsClassifier().Classify([page]);

        Assert.Equal(page.Blocks.Count, changed);
        Assert.All(page.Blocks, block => Assert.True(block.IsExcluded));
    }

    private static DocumentPage Page(int pageNumber, params PageBlock[] blocks)
    {
        var page = new DocumentPage
        {
            PageNumber = pageNumber,
            OriginalImagePath = $"page-{pageNumber}.png",
            ProcessedImagePath = $"page-{pageNumber}.png",
            PreviewImagePath = $"page-{pageNumber}.png",
        };
        page.Blocks.AddRange(blocks);
        return page;
    }

    private static PageBlock Block(string id, BlockType type, string text, int readingOrder, double x = 0.1) => new()
    {
        Id = id,
        PageNumber = 1,
        Bounds = new BoundingBox(x, 0.1 + readingOrder * 0.05, 0.8, 0.03),
        Type = type,
        OcrText = text,
        OcrConfidence = 1,
        ReadingOrder = readingOrder,
    };
}
