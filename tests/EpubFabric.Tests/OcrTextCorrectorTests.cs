using System.Net;
using System.Text;
using EpubFabric.Core.Models;
using EpubFabric.Ollama;

namespace EpubFabric.Tests;

public sealed class OcrTextCorrectorTests
{
    [Fact]
    public async Task CorrectPageAsync_AppliesSmallCharacterFixes()
    {
        var page = CreatePage(CreateBlock("p0001-b0001", "この章では棗境の構築を解脱します。"));
        var responseJson = """{"blocks":[{"id":"p0001-b0001","corrected":"この章では環境の構築を解説します。"}]}""";
        var corrector = CreateCorrector(responseJson);

        var corrected = await corrector.CorrectPageAsync(page);

        Assert.Equal(1, corrected);
        Assert.Equal("この章では環境の構築を解説します。", page.Blocks[0].CorrectedText);
    }

    [Fact]
    public async Task CorrectPageAsync_RejectsRewrittenText()
    {
        // 文字数が変わる修正案（言い換え・要約・挿入・削除）は適用しない。
        var page = CreatePage(CreateBlock("p0001-b0001", "この章では環境の構築手順を順番に解説します。"));
        var responseJson = """{"blocks":[{"id":"p0001-b0001","corrected":"環境構築の説明。"}]}""";
        var corrector = CreateCorrector(responseJson);

        var corrected = await corrector.CorrectPageAsync(page);

        Assert.Equal(0, corrected);
        Assert.Null(page.Blocks[0].CorrectedText);
    }

    [Fact]
    public async Task CorrectPageAsync_RejectsInsertionEvenIfSmall()
    {
        // 実データ検証で確認された改悪: 「なかったため」→「なかったらため」のような1文字挿入。
        var page = CreatePage(CreateBlock("p0001-b0001", "なかったため，原因を辿ることが"));
        var responseJson = """{"blocks":[{"id":"p0001-b0001","corrected":"なかったらため，原因を辿ることが"}]}""";
        var corrector = CreateCorrector(responseJson);

        var corrected = await corrector.CorrectPageAsync(page);

        Assert.Equal(0, corrected);
        Assert.Null(page.Blocks[0].CorrectedText);
    }

    [Fact]
    public async Task CorrectPageAsync_RejectsChangesInsideUrlsAndNumbers()
    {
        // 実データ検証で確認された改悪: arXiv番号の書き換え（2408.06292→2400.06292）。
        var page = CreatePage(CreateBlock("p0001-b0001", "arXiv:2408.06292[cs.AI](2024)"));
        var responseJson = """{"blocks":[{"id":"p0001-b0001","corrected":"arXiv:2400.06292[cs.AI](2024)"}]}""";
        var corrector = CreateCorrector(responseJson);

        var corrected = await corrector.CorrectPageAsync(page);

        Assert.Equal(0, corrected);
        Assert.Null(page.Blocks[0].CorrectedText);
    }

    [Fact]
    public async Task CorrectPageAsync_RejectsHiraganaToHiraganaSubstitution()
    {
        // 実データ検証で確認された改悪: LLMが行断片の文法を「直そう」として
        // 「ればよい」→「ればいる」のようにひらがなを書き換える。OCRの誤認識は
        // 字形の似た漢字・カタカナ・英数字の混同がほとんどのため、ひらがな同士の置換は拒否する。
        var page = CreatePage(CreateBlock("p0001-b0001", "ればよい。研究者自身の創造性や判断を活かした"));
        var responseJson = """{"blocks":[{"id":"p0001-b0001","corrected":"ればいる。研究者自身の創造性や判断を活かした"}]}""";
        var corrector = CreateCorrector(responseJson);

        var corrected = await corrector.CorrectPageAsync(page);

        Assert.Equal(0, corrected);
        Assert.Null(page.Blocks[0].CorrectedText);
    }

    [Fact]
    public async Task CorrectPageAsync_AllowsShortAsciiFixes()
    {
        // 「A1」→「AI」のような短い英数字の誤認識修正は許容する（保護対象は3文字以上の連続列）。
        var page = CreatePage(CreateBlock("p0001-b0001", "そのためには，A1の関与度を明示する"));
        var responseJson = """{"blocks":[{"id":"p0001-b0001","corrected":"そのためには，AIの関与度を明示する"}]}""";
        var corrector = CreateCorrector(responseJson);

        var corrected = await corrector.CorrectPageAsync(page);

        Assert.Equal(1, corrected);
        Assert.Equal("そのためには，AIの関与度を明示する", page.Blocks[0].CorrectedText);
    }

    [Fact]
    public async Task CorrectPageAsync_UnchangedTextIsNotCountedAsCorrection()
    {
        var page = CreatePage(CreateBlock("p0001-b0001", "正しい本文です。"));
        var responseJson = """{"blocks":[{"id":"p0001-b0001","corrected":"正しい本文です。"}]}""";
        var corrector = CreateCorrector(responseJson);

        var corrected = await corrector.CorrectPageAsync(page);

        Assert.Equal(0, corrected);
        Assert.Null(page.Blocks[0].CorrectedText);
    }

    [Fact]
    public async Task CorrectPageAsync_SkipsManuallyCorrectedAndNonOcrBlocks()
    {
        // 9.9節: 手動修正した項目は再解析で上書きしない。PDFテキスト層由来も校正対象外。
        var manual = CreateBlock("p0001-b0001", "棗境");
        manual.CorrectedText = "環境（手動修正済み）";
        var pdfBlock = CreateBlock("p0001-b0002", "PDFテキスト層の本文");
        pdfBlock.TextSource = TextSourceKind.PdfTextLayer;
        var page = CreatePage(manual, pdfBlock);

        var responseJson = """
            {"blocks":[
                {"id":"p0001-b0001","corrected":"環境"},
                {"id":"p0001-b0002","corrected":"PDFテキスト層の本文！"}
            ]}
            """;
        var corrector = CreateCorrector(responseJson);

        var corrected = await corrector.CorrectPageAsync(page);

        Assert.Equal(0, corrected);
        Assert.Equal("環境（手動修正済み）", manual.CorrectedText);
        Assert.Null(pdfBlock.CorrectedText);
    }

    [Fact]
    public async Task CorrectPageAsync_InvalidIdAmongOthers_AppliesValidOnes()
    {
        var page = CreatePage(
            CreateBlock("p0001-b0001", "解脱します。"),
            CreateBlock("p0001-b0002", "本文です。"));

        var responseJson = """
            {"blocks":[
                {"id":"p0001-b0001","corrected":"解説します。"},
                {"id":"unknown","corrected":"無効なID"}
            ]}
            """;
        var corrector = CreateCorrector(responseJson);

        var corrected = await corrector.CorrectPageAsync(page);

        Assert.Equal(1, corrected);
        Assert.Equal("解説します。", page.Blocks[0].CorrectedText);
        Assert.Null(page.Blocks[1].CorrectedText);
    }

    private static OcrTextCorrector CreateCorrector(string ollamaResponseText)
    {
        var handler = new FakeOllamaHandler(ollamaResponseText);
        var httpClient = new HttpClient(handler);
        var client = new OllamaClient("http://localhost:11434", httpClient);
        return new OcrTextCorrector(client, "test-model");
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

    private static PageBlock CreateBlock(string id, string text) => new()
    {
        Id = id,
        PageNumber = 1,
        Bounds = new BoundingBox(0, 0, 1, 1),
        Type = BlockType.Body,
        OcrText = text,
        TextSource = TextSourceKind.Ocr,
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
