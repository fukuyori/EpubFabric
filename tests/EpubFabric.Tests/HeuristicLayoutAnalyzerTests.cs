using EpubFabric.Core.Models;
using EpubFabric.Layout;

namespace EpubFabric.Tests;

public class HeuristicLayoutAnalyzerTests
{
    private readonly HeuristicLayoutAnalyzer _analyzer = new();

    [Fact]
    public void AnalyzePage_LargeLineAboveBody_IsClassifiedAsHeading()
    {
        var lines = new List<TextLine>
        {
            new(new BoundingBox(0.1, 0.10, 0.4, 0.05), "見出し", 0.95), // 本文の約2.2倍の高さ
            new(new BoundingBox(0.1, 0.20, 0.6, 0.0225), "本文1行目です。", 0.95),
            new(new BoundingBox(0.1, 0.23, 0.6, 0.0225), "本文2行目です。", 0.95),
            new(new BoundingBox(0.1, 0.26, 0.6, 0.0225), "本文3行目です。", 0.95),
        };

        var blocks = _analyzer.AnalyzePage(pageNumber: 1, lines);

        var heading = blocks.Single(b => b.OcrText == "見出し");
        Assert.Equal(BlockType.ChapterTitle, heading.Type);
        Assert.Equal(1, heading.HeadingLevel);
        Assert.Equal(0, heading.ReadingOrder);

        Assert.All(blocks.Where(b => b.OcrText != "見出し"), b => Assert.Equal(BlockType.Body, b.Type));
    }

    [Fact]
    public void AnalyzePage_TwoColumnLayout_ReadsLeftColumnFullyBeforeRightColumn()
    {
        var lines = new List<TextLine>
        {
            // 左段（X: 0.05-0.40）を上から下へ
            new(new BoundingBox(0.05, 0.10, 0.35, 0.03), "左段1", 0.9),
            new(new BoundingBox(0.05, 0.14, 0.35, 0.03), "左段2", 0.9),
            new(new BoundingBox(0.05, 0.18, 0.35, 0.03), "左段3", 0.9),
            // 右段（X: 0.55-0.90）を上から下へ
            new(new BoundingBox(0.55, 0.10, 0.35, 0.03), "右段1", 0.9),
            new(new BoundingBox(0.55, 0.14, 0.35, 0.03), "右段2", 0.9),
            new(new BoundingBox(0.55, 0.18, 0.35, 0.03), "右段3", 0.9),
        };

        var blocks = _analyzer.AnalyzePage(pageNumber: 1, lines);
        var orderedTexts = blocks.OrderBy(b => b.ReadingOrder).Select(b => b.OcrText).ToList();

        Assert.Equal(["左段1", "左段2", "左段3", "右段1", "右段2", "右段3"], orderedTexts);
    }

    [Fact]
    public void AnalyzePage_FigureRegionCoveredByCodeLines_BecomesCodeBlockKeepingText()
    {
        // 罫線囲みのコード例: 図として検出されても、テキスト行の被覆率が高くコード記号を
        // 含むならCodeブロックとしてテキストを保持する（0b(a)）。
        var lines = new List<TextLine>
        {
            new(new BoundingBox(0.12, 0.12, 0.6, 0.04), "awk '{ print $1 }' data.txt", 0.9),
            new(new BoundingBox(0.12, 0.17, 0.6, 0.04), "awk -F: '{ sum += $3 } END { print sum }'", 0.9),
            new(new BoundingBox(0.10, 0.40, 0.7, 0.03), "本文の一行目がここにあります。", 0.9),
        };
        var regions = new List<NonTextRegion>
        {
            new(new BoundingBox(0.10, 0.10, 0.65, 0.13), NonTextRegionKind.Figure),
        };

        var blocks = _analyzer.AnalyzePage(pageNumber: 1, lines, regions);

        var codeBlock = Assert.Single(blocks, b => b.Type == BlockType.Code);
        Assert.Contains("awk '{ print $1 }' data.txt", codeBlock.OcrText);
        Assert.Contains("\n", codeBlock.OcrText);
        Assert.DoesNotContain(blocks, b => b.Type == BlockType.Figure);
    }

    [Fact]
    public void AnalyzePage_FigureRegionWithSparseLabels_StaysFigure()
    {
        // 写真・図解: ラベルが少なく被覆率が低い領域は従来どおりFigure（ラベル行は破棄）。
        var lines = new List<TextLine>
        {
            new(new BoundingBox(0.30, 0.25, 0.08, 0.02), "図中ラベル", 0.9),
            new(new BoundingBox(0.10, 0.60, 0.7, 0.03), "本文の一行目がここにあります。", 0.9),
        };
        var regions = new List<NonTextRegion>
        {
            new(new BoundingBox(0.10, 0.10, 0.65, 0.40), NonTextRegionKind.Figure),
        };

        var blocks = _analyzer.AnalyzePage(pageNumber: 1, lines, regions);

        Assert.Single(blocks, b => b.Type == BlockType.Figure);
        Assert.DoesNotContain(blocks, b => b.Type == BlockType.Code);
        Assert.DoesNotContain(blocks, b => b.OcrText == "図中ラベル");
    }

    [Fact]
    public void AnalyzePage_BoldShortLineWithBodyHeight_IsClassifiedAsSubheading()
    {
        // 高さは本文と同じだがインク密度が明確に高い短行 = ゴシック太字見出し（0b(c)）。
        var lines = new List<TextLine>
        {
            new(new BoundingBox(0.1, 0.10, 0.3, 0.03), "太字の見出し", 0.9, TextSourceKind.Ocr, InkDensity: 0.30),
            new(new BoundingBox(0.1, 0.15, 0.7, 0.03), "本文の一行目がここにあります。", 0.9, TextSourceKind.Ocr, InkDensity: 0.15),
            new(new BoundingBox(0.1, 0.19, 0.7, 0.03), "本文の二行目がここにあります。", 0.9, TextSourceKind.Ocr, InkDensity: 0.16),
            new(new BoundingBox(0.1, 0.23, 0.7, 0.03), "本文の三行目がここにあります。", 0.9, TextSourceKind.Ocr, InkDensity: 0.14),
            new(new BoundingBox(0.1, 0.27, 0.7, 0.03), "本文の四行目がここにあります。", 0.9, TextSourceKind.Ocr, InkDensity: 0.15),
            new(new BoundingBox(0.1, 0.31, 0.7, 0.03), "本文の五行目がここにあります。", 0.9, TextSourceKind.Ocr, InkDensity: 0.16),
        };

        var blocks = _analyzer.AnalyzePage(pageNumber: 1, lines);

        Assert.Equal(BlockType.Subheading, blocks.Single(b => b.OcrText == "太字の見出し").Type);
        Assert.All(blocks.Where(b => b.OcrText != "太字の見出し"), b => Assert.Equal(BlockType.Body, b.Type));
    }

    [Fact]
    public void AnalyzePage_DensityNotMeasured_KeepsHeightBasedClassificationOnly()
    {
        // InkDensity未測定（null）の行しかない場合は、太字判定を行わず従来どおり。
        var lines = new List<TextLine>
        {
            new(new BoundingBox(0.1, 0.10, 0.3, 0.03), "短い行", 0.9),
            new(new BoundingBox(0.1, 0.15, 0.7, 0.03), "本文の一行目がここにあります。", 0.9),
            new(new BoundingBox(0.1, 0.19, 0.7, 0.03), "本文の二行目がここにあります。", 0.9),
            new(new BoundingBox(0.1, 0.23, 0.7, 0.03), "本文の三行目がここにあります。", 0.9),
            new(new BoundingBox(0.1, 0.27, 0.7, 0.03), "本文の四行目がここにあります。", 0.9),
        };

        var blocks = _analyzer.AnalyzePage(pageNumber: 1, lines);

        Assert.All(blocks, b => Assert.Equal(BlockType.Body, b.Type));
    }

    [Fact]
    public void AnalyzePage_ShortLineNearBottomAllDigits_IsClassifiedAsPageNumberAndExcluded()
    {
        var lines = new List<TextLine>
        {
            new(new BoundingBox(0.1, 0.20, 0.6, 0.03), "本文です。", 0.9),
            new(new BoundingBox(0.48, 0.96, 0.04, 0.02), "8", 0.9),
        };

        var blocks = _analyzer.AnalyzePage(pageNumber: 1, lines);

        var pageNumberBlock = blocks.Single(b => b.OcrText == "8");
        Assert.Equal(BlockType.PageNumber, pageNumberBlock.Type);
        Assert.True(pageNumberBlock.IsExcluded);
    }

    [Fact]
    public void AnalyzePage_NoLines_ReturnsEmpty()
    {
        var blocks = _analyzer.AnalyzePage(pageNumber: 1, []);

        Assert.Empty(blocks);
    }

    [Fact]
    public void AnalyzePage_LineInsideFigureRegion_IsDroppedNotDuplicatedAsBody()
    {
        // 図の内部にあるOCR行（図中のラベルなど）は、切り出した図画像に既に含まれる
        // ため、別の本文段落として重複させてはならない。
        var lines = new List<TextLine>
        {
            new(new BoundingBox(0.15, 0.15, 0.2, 0.02), "図中のラベル", 0.9),
            new(new BoundingBox(0.1, 0.60, 0.6, 0.03), "図の外の本文です。", 0.9),
        };
        var regions = new List<NonTextRegion>
        {
            new(new BoundingBox(0.1, 0.10, 0.6, 0.30), NonTextRegionKind.Figure),
        };

        var blocks = _analyzer.AnalyzePage(pageNumber: 1, lines, regions);

        Assert.DoesNotContain(blocks, b => b.OcrText == "図中のラベル");
        Assert.Contains(blocks, b => b.OcrText == "図の外の本文です。");
        Assert.Single(blocks, b => b.Type == BlockType.Figure);
    }

    [Fact]
    public void AnalyzePage_FigureRegion_ProducesFigureBlockInReadingOrder()
    {
        var lines = new List<TextLine>
        {
            new(new BoundingBox(0.1, 0.60, 0.6, 0.03), "図の下の本文です。", 0.9),
        };
        var regions = new List<NonTextRegion>
        {
            new(new BoundingBox(0.1, 0.10, 0.6, 0.30), NonTextRegionKind.Figure),
        };

        var blocks = _analyzer.AnalyzePage(pageNumber: 1, lines, regions);

        var figure = Assert.Single(blocks, b => b.Type == BlockType.Figure);
        Assert.Equal(string.Empty, figure.OcrText);
        Assert.True(figure.ReadingOrder < blocks.Single(b => b.Type == BlockType.Body).ReadingOrder);
    }

    [Fact]
    public void AnalyzePage_LineInsideBoxedRegion_IsClassifiedAsAside()
    {
        var lines = new List<TextLine>
        {
            new(new BoundingBox(0.1, 0.10, 0.6, 0.03), "通常の本文です。", 0.9),
            new(new BoundingBox(0.12, 0.30, 0.5, 0.03), "囲み記事内のテキストです。", 0.9),
        };
        var regions = new List<NonTextRegion>
        {
            new(new BoundingBox(0.1, 0.28, 0.6, 0.10), NonTextRegionKind.Boxed),
        };

        var blocks = _analyzer.AnalyzePage(pageNumber: 1, lines, regions);

        Assert.Equal(BlockType.Body, blocks.Single(b => b.OcrText == "通常の本文です。").Type);
        Assert.Equal(BlockType.Aside, blocks.Single(b => b.OcrText == "囲み記事内のテキストです。").Type);
    }

    [Fact]
    public void AnalyzePage_ShortLineDirectlyBelowFigure_IsLinkedAsCaption()
    {
        var lines = new List<TextLine>
        {
            new(new BoundingBox(0.1, 0.42, 0.5, 0.025), "図1 実験結果のグラフ", 0.9),
            new(new BoundingBox(0.1, 0.60, 0.6, 0.03), "本文が続きます。", 0.9),
        };
        var regions = new List<NonTextRegion>
        {
            new(new BoundingBox(0.1, 0.10, 0.6, 0.30), NonTextRegionKind.Figure),
        };

        var blocks = _analyzer.AnalyzePage(pageNumber: 1, lines, regions);

        var figure = blocks.Single(b => b.Type == BlockType.Figure);
        var caption = blocks.Single(b => b.OcrText == "図1 実験結果のグラフ");
        var body = blocks.Single(b => b.OcrText == "本文が続きます。");

        Assert.Equal(BlockType.Caption, caption.Type);
        Assert.Equal(figure.Id, caption.RelatedBlockId);
        Assert.Equal(BlockType.Body, body.Type);
        Assert.Null(body.RelatedBlockId);
    }
}
