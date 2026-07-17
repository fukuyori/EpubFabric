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
