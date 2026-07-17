using System.Net;
using System.Text;
using EpubFabric.Core.Models;
using EpubFabric.Ollama;

namespace EpubFabric.Tests;

public class PageBlockClassifierTests
{
    [Fact]
    public async Task ClassifyPageAsync_SuggestsExcludingPreviouslyIncludedBlock_FlagsForReviewInsteadOfExcluding()
    {
        // 9.9節「EPUBへの収録有無」は人手校正の対象であり、Ollamaの判定だけで
        // 本文ブロックがEPUBから消えてはならない（実データ検証で見つかった実際の事故：
        // 記事タイトルがFooterと誤判定され除外されかけた）。
        var page = CreatePage(CreateBlock("p0001-b0001", BlockType.Body, "本文タイトルです"));
        var responseJson = """{"blocks":[{"id":"p0001-b0001","type":"footer","headingLevel":0,"confidence":0.9}]}""";
        var classifier = CreateClassifier(responseJson);

        await classifier.ClassifyPageAsync(page);

        var block = page.Blocks.Single();
        Assert.Equal(BlockType.Footer, block.Type);
        Assert.False(block.IsExcluded);
        Assert.True(block.RequiresReview);
    }

    [Fact]
    public async Task ClassifyPageAsync_OneInvalidIdAmongOthers_AppliesTheValidOnesInstadOfDiscardingAll()
    {
        // 実データ検証で見つかった実際の不具合：モデルがIDの先頭の"p"を欠落させた1件のせいで
        // 応答全体が無効と判定され、他の正しい分類結果まで捨てられていた。
        var page = CreatePage(
            CreateBlock("p0001-b0001", BlockType.Body, "見出しらしき短文"),
            CreateBlock("p0001-b0002", BlockType.Body, "本文です"));

        var responseJson = """
            {"blocks":[
                {"id":"p0001-b0001","type":"section_heading","headingLevel":2,"confidence":0.9},
                {"id":"0001-b0002","type":"body","headingLevel":0,"confidence":0.9}
            ]}
            """;
        var classifier = CreateClassifier(responseJson);

        var changed = await classifier.ClassifyPageAsync(page);

        Assert.Equal(1, changed);
        Assert.Equal(BlockType.SectionHeading, page.Blocks[0].Type);
        Assert.Equal(BlockType.Body, page.Blocks[1].Type); // 無効なIDの項目は無視され、元の分類のまま。
    }

    [Fact]
    public async Task ClassifyPageAsync_OutOfRangeHeadingLevelAndDuplicateId_SkipsOnlyThoseEntries()
    {
        // 実データ検証で見つかった実際の不具合：モデルがheadingLevelに1000000や
        // 1000000000000000（Int32の範囲外）を返し、さらに同じIDを2回返すことがあった。
        // これらの不正な項目だけを読み捨て、残りの正しい分類結果は適用されるべき。
        var page = CreatePage(
            CreateBlock("p0001-b0001", BlockType.Body, "本文です"),
            CreateBlock("p0001-b0002", BlockType.Body, "見出しらしき短文"));

        var responseJson = """
            {"blocks":[
                {"id":"p0001-b0001","type":"body","headingLevel":1000000,"confidence":0.9},
                {"id":"p0001-b0002","type":"section_heading","headingLevel":2,"confidence":0.9},
                {"id":"p0001-b0002","type":"body","headingLevel":1000000000000000,"confidence":1000000000000000}
            ]}
            """;
        var classifier = CreateClassifier(responseJson);

        var changed = await classifier.ClassifyPageAsync(page);

        Assert.Equal(1, changed);
        Assert.Equal(BlockType.Body, page.Blocks[0].Type); // 範囲外headingLevelの項目は無視され、元のまま。
        Assert.Equal(BlockType.SectionHeading, page.Blocks[1].Type); // 最初に現れた有効な項目が採用される。
    }

    private static PageBlockClassifier CreateClassifier(string ollamaResponseText)
    {
        var handler = new FakeOllamaHandler(ollamaResponseText);
        var httpClient = new HttpClient(handler);
        var client = new OllamaClient("http://localhost:11434", httpClient);
        return new PageBlockClassifier(client, "test-model");
    }

    private static DocumentPage CreatePage(params PageBlock[] blocks)
    {
        var page = new DocumentPage
        {
            PageNumber = 1,
            OriginalImagePath = "page-1.png",
            ProcessedImagePath = "page-1.png",
            PreviewImagePath = "page-1.png",
        };
        page.Blocks.AddRange(blocks);
        return page;
    }

    private static PageBlock CreateBlock(string id, BlockType type, string text) => new()
    {
        Id = id,
        PageNumber = 1,
        Bounds = new BoundingBox(0, 0, 1, 1),
        Type = type,
        OcrText = text,
        ReadingOrder = 0,
    };

    private sealed class FakeOllamaHandler(string responseText) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = $$"""{"model":"test-model","created_at":"2026-01-01T00:00:00Z","response":{{System.Text.Json.JsonSerializer.Serialize(responseText)}},"done":true}""";
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
