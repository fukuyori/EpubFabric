using EpubFabric.Core.Models;
using EpubFabric.Layout;

namespace EpubFabric.Tests;

public class ParagraphMergerTests
{
    [Theory]
    [InlineData(TextSourceKind.NdlOcr, 1)]
    [InlineData(TextSourceKind.Ocr, 2)]
    public void Merge_ScalesTightNdlOcrLineBoxesForLineSpacing(TextSourceKind source, int expectedCount)
    {
        // 科学2026年1月号3ページの実測値。NDLOCR-Liteの行矩形は字面の高さ（0.0116）で、
        // 行送り0.0222に対する隙間（0.0106）が高さの0.8倍を超える。NDLOCR-Liteの行だけ
        // 行矩形の大きさの違いを補正して連結する。同じ座標のRapidOCR行は従来どおり分ける。
        var blocks = new List<PageBlock>
        {
            HorizontalLine("b1", y: 0.1210, height: 0.0116, text: "を遂行したこの論文原稿は，", source, readingOrder: 0),
            HorizontalLine("b2", y: 0.1432, height: 0.0116, text: "や数値実験コードの生成と", source, readingOrder: 1),
        };

        var merged = new ParagraphMerger().Merge(blocks, WritingMode.Horizontal);

        Assert.Equal(expectedCount, merged.Count);
    }

    [Fact]
    public void Merge_KeepsNdlOcrParagraphSpacingApart()
    {
        // 補正後も、1行分の空きがある段落間は連結しない。
        var blocks = new List<PageBlock>
        {
            HorizontalLine("b1", y: 0.1210, height: 0.0116, text: "前の段落の最後の行。", TextSourceKind.NdlOcr, readingOrder: 0),
            HorizontalLine("b2", y: 0.1654, height: 0.0116, text: "次の段落の最初の行", TextSourceKind.NdlOcr, readingOrder: 1),
        };

        var merged = new ParagraphMerger().Merge(blocks, WritingMode.Horizontal);

        Assert.Equal(2, merged.Count);
    }

    [Theory]
    [InlineData(0.707, 2)]
    [InlineData(1.0, 1)]
    public void Merge_ConvertsVerticalColumnWidthByPageAspectForIndent(double pageAspectRatio, int expectedCount)
    {
        // 地理2026年1月号16ページの実測値。前の列は文末で終わり、次の列は1字下げ（ページ高さ基準0.0153）で始まる。
        // 列の太さ（ページ幅基準0.023）をそのまま使うとしきい値が0.0207となり字下げを見逃す。
        // 幅/高さ（約0.707）で換算すると0.0146となり、字下げとして分かれる。
        var blocks = new List<PageBlock>
        {
            NdlColumn("b1", x: 0.738, y: 0.6753, width: 0.022, height: 0.2447, text: "なっており、御嶽講を専業にする", readingOrder: 0),
            NdlColumn("b2", x: 0.700, y: 0.6753, width: 0.021, height: 0.1523, text: "人はいないそうです。", readingOrder: 1),
            NdlColumn("b3", x: 0.660, y: 0.6906, width: 0.023, height: 0.2294, text: "講の流行った時期、山を模した", readingOrder: 2),
            NdlColumn("b4", x: 0.621, y: 0.6757, width: 0.023, height: 0.2342, text: "ものを身近な場所につくること、", readingOrder: 3),
        };

        var merged = new ParagraphMerger().Merge(blocks, WritingMode.Vertical, pageAspectRatio);

        Assert.Equal(expectedCount, merged.Count);
        Assert.Equal("なっており、御嶽講を専業にする人はいないそうです。", merged[0].OcrText[..25]);
    }

    [Fact]
    public void Merge_KeepsVerticalHangingIndentInsideNote()
    {
        // 注のぶら下がり字下げ。2列目は1字下がるが、前の列が文の途中で終わるので続きとして連結する。
        var blocks = new List<PageBlock>
        {
            NdlColumn("b1", x: 0.738, y: 0.6753, width: 0.022, height: 0.2447, text: "(2)筆者は子連れのムスリマという属性", readingOrder: 0),
            NdlColumn("b2", x: 0.700, y: 0.6906, width: 0.022, height: 0.2294, text: "から、「ダイバーシティ要員」のよう", readingOrder: 1),
        };

        var merged = new ParagraphMerger().Merge(blocks, WritingMode.Vertical, pageAspectRatio: 0.707);

        Assert.Single(merged);
    }

    private static PageBlock NdlColumn(
        string id, double x, double y, double width, double height, string text, int readingOrder) => new()
    {
        Id = id,
        PageNumber = 1,
        Bounds = new BoundingBox(x, y, width, height),
        Type = BlockType.Body,
        OcrText = text,
        OcrConfidence = 0.95,
        TextSource = TextSourceKind.NdlOcr,
        ReadingOrder = readingOrder,
    };

    private static PageBlock HorizontalLine(
        string id, double y, double height, string text, TextSourceKind source, int readingOrder) => new()
    {
        Id = id,
        PageNumber = 1,
        Bounds = new BoundingBox(0.08, y, 0.70, height),
        Type = BlockType.Body,
        OcrText = text,
        OcrConfidence = 0.95,
        TextSource = source,
        ReadingOrder = readingOrder,
    };

    [Fact]
    public void Merge_JoinsVerticalBodyAcrossBandsWhenTheWordIsCut()
    {
        // 縦書きページで図版が挟まり、本文が上下の帯へ分かれた形。
        // 上の帯の左端の列から、下の帯の右端の列へ語の途中で続いている。
        var blocks = new List<PageBlock>
        {
            VerticalColumn("b1", column: 0.20, top: 0.08, bottom: 0.40, text: "周囲のまなざしによっ", readingOrder: 0),
            VerticalColumn("b2", column: 0.80, top: 0.55, bottom: 0.92, text: "て深まる。", readingOrder: 1),
        };

        var merged = new ParagraphMerger().Merge(blocks, WritingMode.Vertical);

        Assert.Equal("周囲のまなざしによって深まる。", Assert.Single(merged).OcrText);
    }

    [Fact]
    public void Merge_KeepsVerticalBandsApartWhenTheSentenceIsComplete()
    {
        var blocks = new List<PageBlock>
        {
            VerticalColumn("b1", column: 0.20, top: 0.08, bottom: 0.40, text: "前の段はここで終わる。", readingOrder: 0),
            VerticalColumn("b2", column: 0.80, top: 0.55, bottom: 0.92, text: "次の段は別の話題である。", readingOrder: 1),
        };

        var merged = new ParagraphMerger().Merge(blocks, WritingMode.Vertical);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_KeepsTableLikeCellsApartAcrossBands()
    {
        // 表の升目は数字で終わる・始まるため、帯をまたいでも連結しない。
        var blocks = new List<PageBlock>
        {
            VerticalColumn("b1", column: 0.20, top: 0.08, bottom: 0.40, text: "世界の国々について", readingOrder: 0),
            VerticalColumn("b2", column: 0.80, top: 0.55, bottom: 0.92, text: "52.6", readingOrder: 1),
        };

        var merged = new ParagraphMerger().Merge(blocks, WritingMode.Vertical);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_DoesNotJoinAsideAcrossBands()
    {
        // 本文どうしに限る。囲み記事は語の途中で切れていても連結しない。
        var blocks = new List<PageBlock>
        {
            VerticalColumn("b1", column: 0.20, top: 0.08, bottom: 0.40, text: "囲み記事の途中で", type: BlockType.Aside, readingOrder: 0),
            VerticalColumn("b2", column: 0.80, top: 0.55, bottom: 0.92, text: "切れている", type: BlockType.Aside, readingOrder: 1),
        };

        var merged = new ParagraphMerger().Merge(blocks, WritingMode.Vertical);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_JoinsHorizontalColumnsWhenTheWordIsCut()
    {
        // 横書き2段組みで、左段の最終行から右段の先頭行へ語の途中で続く形。
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.88, "これらは制度", x: 0.06, width: 0.40, readingOrder: 0),
            Line("b2", 0.12, "設計や実施方法によって", x: 0.54, width: 0.40, readingOrder: 1),
        };

        var merged = new ParagraphMerger().Merge(blocks);

        Assert.Equal("これらは制度設計や実施方法によって", Assert.Single(merged).OcrText);
    }

    [Fact]
    public void Merge_KeepsSideBySideColumnsApartAtTheSameHeight()
    {
        // 同じ高さで横に並ぶ段は、語の途中に見えても連結しない。
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.14, "左段の本文", x: 0.05, width: 0.40, readingOrder: 0),
            Line("b2", 0.14, "右段の本文", x: 0.55, width: 0.40, readingOrder: 1),
        };

        var merged = new ParagraphMerger().Merge(blocks);

        Assert.Equal(2, merged.Count);
    }

    /// <summary>縦書きの1列。<paramref name="column"/>が大きいほど紙面の右側に置く。</summary>
    private static PageBlock VerticalColumn(
        string id,
        double column,
        double top,
        double bottom,
        string text,
        BlockType type = BlockType.Body,
        int readingOrder = 0) => new()
        {
            Id = id,
            PageNumber = 1,
            Bounds = new BoundingBox(column, top, 0.03, bottom - top),
            Type = type,
            OcrText = text,
            OcrConfidence = 0.95,
            ReadingOrder = readingOrder,
        };

    [Fact]
    public void Merge_JoinsDigitsSplitAcrossAdjacentLinesWithoutSpace()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.1, "19", width: 0.7, height: 0.03, readingOrder: 0),
            Line("b2", 0.135, "88年生まれ", width: 0.7, height: 0.03, readingOrder: 1),
        };

        var result = new ParagraphMerger().Merge(blocks);

        Assert.Equal("1988年生まれ", Assert.Single(result).OcrText);
    }

    private static PageBlock Line(string id, double y, string text, BlockType type = BlockType.Body, double x = 0.1, double width = 0.8, double height = 0.02, int readingOrder = 0, int pageNumber = 1) => new()
    {
        Id = id,
        PageNumber = pageNumber,
        Bounds = new BoundingBox(x, y, width, height),
        Type = type,
        OcrText = text,
        OcrConfidence = 0.95,
        ReadingOrder = readingOrder,
    };

    [Fact]
    public void Merge_JoinsConsecutiveBodyLinesIntoParagraph()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "一行目の本文で", readingOrder: 0),
            Line("b2", 0.125, "二行目に続く。", readingOrder: 1),
            Line("b3", 0.15, "Latin text", readingOrder: 2),
        };

        var merged = new ParagraphMerger().Merge(blocks);

        var paragraph = Assert.Single(merged);
        Assert.Equal("一行目の本文で二行目に続く。Latin text", paragraph.OcrText);
        Assert.Equal(0.10, paragraph.Bounds.Y, precision: 5);
        Assert.Equal(0.07, paragraph.Bounds.Height, precision: 5);
        Assert.Equal(0, paragraph.ReadingOrder);
    }

    [Fact]
    public void Merge_StartsNewParagraphOnLargeGapOrIndent()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "第一段落。", readingOrder: 0),
            // 行間の3倍の隙間 → 新しい段落。
            Line("b2", 0.19, "第二段落。", readingOrder: 1),
            // 字下げ → 新しい段落。
            Line("b3", 0.215, "　第三段落。", x: 0.14, width: 0.76, readingOrder: 2),
        };

        var merged = new ParagraphMerger().Merge(blocks);

        Assert.Equal(3, merged.Count);
        Assert.Equal([0, 1, 2], merged.Select(b => b.ReadingOrder));
    }

    [Fact]
    public void Merge_DoesNotMergeAcrossTypesColumnsOrHeadings()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "見出し", BlockType.SectionHeading, height: 0.03, readingOrder: 0),
            Line("b2", 0.14, "左段の本文", x: 0.05, width: 0.4, readingOrder: 1),
            // 同じ高さの右段 → 横に並んでいるので統合しない。
            Line("b3", 0.14, "右段の本文", x: 0.55, width: 0.4, readingOrder: 2),
            Line("b4", 0.18, "囲み記事", BlockType.Aside, readingOrder: 3),
        };

        var merged = new ParagraphMerger().Merge(blocks);

        Assert.Equal(4, merged.Count);
    }

    [Fact]
    public void Merge_DoesNotInsertSpaceAfterLineEndHyphen()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "hardware-", readingOrder: 0),
            Line("b2", 0.125, "independent standard", readingOrder: 1),
        };

        var paragraph = Assert.Single(new ParagraphMerger().Merge(blocks));

        Assert.Equal("hardware-independent standard", paragraph.OcrText);
    }

    [Fact]
    public void Merge_InsertsSpaceAfterCurlyClosingQuoteInEnglish()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "the words ‘culture’, ‘art’", readingOrder: 0),
            Line("b2", 0.125, "and ‘philosophy’ a lot.", readingOrder: 1),
        };

        var paragraph = Assert.Single(new ParagraphMerger().Merge(blocks));

        Assert.Equal("the words ‘culture’, ‘art’ and ‘philosophy’ a lot.", paragraph.OcrText);
    }

    [Fact]
    public void Merge_AllowsGlyphHeightVariationWithinSameFont()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "number-crunching", height: 0.012, readingOrder: 0),
            Line("b2", 0.1165, "behemoths and database back ends.", height: 0.0083, readingOrder: 1),
        };

        var paragraph = Assert.Single(new ParagraphMerger().Merge(blocks));

        Assert.Equal("number-crunching behemoths and database back ends.", paragraph.OcrText);
    }

    [Fact]
    public void Merge_JoinsConsecutiveHeadingLinesIntoOneHeading()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "複数行にまたがる", BlockType.Subheading, height: 0.03, readingOrder: 0),
            Line("b2", 0.135, "太字の小見出し", BlockType.Subheading, height: 0.03, readingOrder: 1),
        };

        var heading = Assert.Single(new ParagraphMerger().Merge(blocks));

        Assert.Equal(BlockType.Subheading, heading.Type);
        Assert.Equal("複数行にまたがる太字の小見出し", heading.OcrText);
    }

    [Fact]
    public void Merge_LeavesChapterTitleFragmentsForPageWideOrdering()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "言葉に囚", BlockType.ChapterTitle, x: 0.1, width: 0.3, height: 0.05, readingOrder: 0),
            Line("b2", 0.16, "人工無能について", BlockType.ChapterTitle, x: 0.1, width: 0.4, height: 0.05, readingOrder: 1),
        };

        var headings = new ParagraphMerger().Merge(blocks);

        Assert.Equal(2, headings.Count);
    }

    [Fact]
    public void Merge_JoinsSectionHeadingFragmentsFromSameExternalRegion()
    {
        var first = Line(
            "b1", 0.10, "言葉に囚", BlockType.SectionHeading,
            x: 0.10, width: 0.30, height: 0.07, readingOrder: 0);
        var second = Line(
            "b2", 0.10, "われた私たちの科学と", BlockType.SectionHeading,
            x: 0.42, width: 0.40, height: 0.07, readingOrder: 1);
        first.SourceRegionId = "pp:1:doc_title";
        second.SourceRegionId = "pp:1:doc_title";

        var heading = Assert.Single(new ParagraphMerger().Merge([first, second]));

        Assert.Equal("言葉に囚われた私たちの科学と", heading.OcrText);
    }

    [Fact]
    public void Merge_JoinsChapterTitleFragmentsFromSameExternalRegion()
    {
        var first = Line(
            "b1", 0.10, "言葉に囚", BlockType.ChapterTitle,
            x: 0.10, width: 0.30, height: 0.07, readingOrder: 0);
        var second = Line(
            "b2", 0.10, "われた私たちの科学と", BlockType.ChapterTitle,
            x: 0.42, width: 0.40, height: 0.07, readingOrder: 1);
        first.SourceRegionId = "pp:1:doc_title";
        second.SourceRegionId = "pp:1:doc_title";

        var heading = Assert.Single(new ParagraphMerger().Merge([first, second]));

        Assert.Equal(BlockType.ChapterTitle, heading.Type);
        Assert.Equal("言葉に囚われた私たちの科学と", heading.OcrText);
    }

    [Fact]
    public void Merge_VerticalText_JoinsLinesFromRightToLeft()
    {
        var blocks = new List<PageBlock>
        {
            Line("b1", 0.10, "右側の縦書き本文", x: 0.70, width: 0.03, height: 0.50, readingOrder: 0),
            Line("b2", 0.10, "左隣の本文です。", x: 0.66, width: 0.03, height: 0.50, readingOrder: 1),
        };

        var paragraph = Assert.Single(new ParagraphMerger().Merge(blocks, WritingMode.Vertical));

        Assert.Equal("右側の縦書き本文左隣の本文です。", paragraph.OcrText);
        Assert.Equal(0.66, paragraph.Bounds.X, precision: 5);
        Assert.Equal(0.07, paragraph.Bounds.Width, precision: 5);
    }

    [Fact]
    public void MergeAcrossPages_JoinsLowercaseContinuationAtPageBoundary()
    {
        var previous = Line("p1-b1", 0.75, "This sentence continues", readingOrder: 0);
        previous.TextSource = TextSourceKind.PdfTextLayer;
        var next = Line("p2-b1", 0.10, "on the next page.", readingOrder: 0, pageNumber: 2);
        next.TextSource = TextSourceKind.PdfTextLayer;
        var pages = new[] { Page(1, previous), Page(2, next) };

        var count = new ParagraphMerger().MergeAcrossPages(pages);

        Assert.Equal(1, count);
        Assert.Equal("This sentence continues on the next page.", previous.OcrText);
        Assert.Empty(pages[1].Blocks);
    }

    [Fact]
    public void MergeAcrossPages_DoesNotJoinNewParagraphOrHeading()
    {
        var previous = Line("p1-b1", 0.75, "A complete paragraph.", readingOrder: 0);
        var next = Line("p2-b1", 0.10, "New paragraph", readingOrder: 0, pageNumber: 2);
        var pages = new[] { Page(1, previous), Page(2, next) };

        var count = new ParagraphMerger().MergeAcrossPages(pages);

        Assert.Equal(0, count);
        Assert.Single(pages[1].Blocks);
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
}
