using EpubFabric.Core.Models;
using EpubFabric.Document;

namespace EpubFabric.Tests;

public class DocumentBuilderTests
{
    [Fact]
    public void BuildChapters_OrdersBlocksByPageThenReadingOrder_AndSkipsExcluded()
    {
        var pages = new List<DocumentPage>
        {
            CreatePage(pageNumber: 2, ("p2-b2", 1, false), ("p2-b1", 0, false)),
            CreatePage(pageNumber: 1, ("p1-b1", 0, false), ("p1-b2", 1, true)),
        };

        var chapters = new DocumentBuilder().BuildChapters(pages, "テスト書籍");

        var chapter = Assert.Single(chapters);
        Assert.Equal("テスト書籍", chapter.Title);
        Assert.Equal(["p1-b1", "p2-b1", "p2-b2"], chapter.BlockIds);
    }

    [Fact]
    public void BuildChapters_SplitsAtChapterTitleOnly_AndKeepsSectionHeadingInBody()
    {
        var page = new DocumentPage
        {
            PageNumber = 1,
            OriginalImagePath = "page-1.png",
            ProcessedImagePath = "page-1.png",
            PreviewImagePath = "page-1.png",
        };
        page.Blocks.Add(CreateBlock("b1", BlockType.ChapterTitle, "第1章", readingOrder: 0, headingLevel: 1));
        page.Blocks.Add(CreateBlock("b2", BlockType.Body, "本文A", readingOrder: 1));
        page.Blocks.Add(CreateBlock("b3", BlockType.SectionHeading, "第1章 第1節", readingOrder: 2, headingLevel: 2));
        page.Blocks.Add(CreateBlock("b4", BlockType.Body, "本文B", readingOrder: 3));
        page.Blocks.Add(CreateBlock("b5", BlockType.ChapterTitle, "第2章", readingOrder: 4, headingLevel: 1));
        page.Blocks.Add(CreateBlock("b6", BlockType.Body, "本文C", readingOrder: 5));

        var chapters = new DocumentBuilder().BuildChapters([page], "テスト書籍");

        Assert.Equal(2, chapters.Count);

        Assert.Equal("第1章", chapters[0].Title);
        Assert.Equal(1, chapters[0].HeadingLevel);
        // 節見出し（b3）は章の区切りにせず、章内の<h2>として本文に残す。
        Assert.Equal(["b2", "b3", "b4"], chapters[0].BlockIds);

        Assert.Equal("第2章", chapters[1].Title);
        Assert.Equal(["b6"], chapters[1].BlockIds);
    }

    [Fact]
    public void BuildChapters_ContentBeforeFirstHeading_UsesFallbackTitle()
    {
        var page = new DocumentPage
        {
            PageNumber = 1,
            OriginalImagePath = "page-1.png",
            ProcessedImagePath = "page-1.png",
            PreviewImagePath = "page-1.png",
        };
        page.Blocks.Add(CreateBlock("b1", BlockType.Body, "表紙情報", readingOrder: 0));
        page.Blocks.Add(CreateBlock("b2", BlockType.ChapterTitle, "第1章", readingOrder: 1, headingLevel: 1));
        page.Blocks.Add(CreateBlock("b3", BlockType.Body, "本文", readingOrder: 2));

        var chapters = new DocumentBuilder().BuildChapters([page], "テスト書籍");

        Assert.Equal(2, chapters.Count);
        Assert.Equal("テスト書籍", chapters[0].Title);
        Assert.Equal(["b1"], chapters[0].BlockIds);
        Assert.Equal("第1章", chapters[1].Title);
        Assert.Equal(["b3"], chapters[1].BlockIds);
    }

    [Fact]
    public void BuildChapters_HeadingWithManualCorrection_UsesCorrectedTextAsTitle()
    {
        var page = new DocumentPage
        {
            PageNumber = 1,
            OriginalImagePath = "page-1.png",
            ProcessedImagePath = "page-1.png",
            PreviewImagePath = "page-1.png",
        };
        var heading = CreateBlock("b1", BlockType.ChapterTitle, "誤認識された見出し", readingOrder: 0, headingLevel: 1);
        heading.CorrectedText = "校正済みの見出し";
        page.Blocks.Add(heading);
        page.Blocks.Add(CreateBlock("b2", BlockType.Body, "本文", readingOrder: 1));

        var chapters = new DocumentBuilder().BuildChapters([page], "テスト書籍");

        Assert.Equal("校正済みの見出し", Assert.Single(chapters).Title);
    }

    private static PageBlock CreateBlock(string id, BlockType type, string text, int readingOrder, int? headingLevel = null) => new()
    {
        Id = id,
        PageNumber = 1,
        Bounds = new BoundingBox(0, 0, 1, 1),
        Type = type,
        OcrText = text,
        ReadingOrder = readingOrder,
        HeadingLevel = headingLevel,
    };

    private static DocumentPage CreatePage(int pageNumber, params (string Id, int ReadingOrder, bool IsExcluded)[] blocks)
    {
        var page = new DocumentPage
        {
            PageNumber = pageNumber,
            OriginalImagePath = $"page-{pageNumber}.png",
            ProcessedImagePath = $"page-{pageNumber}.png",
            PreviewImagePath = $"page-{pageNumber}.png",
        };

        foreach (var (id, readingOrder, isExcluded) in blocks)
        {
            page.Blocks.Add(new PageBlock
            {
                Id = id,
                PageNumber = pageNumber,
                Bounds = new BoundingBox(0, 0, 1, 1),
                Type = BlockType.Body,
                ReadingOrder = readingOrder,
                IsExcluded = isExcluded,
            });
        }

        return page;
    }
}
