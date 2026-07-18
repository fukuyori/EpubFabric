using EpubFabric.Core.Models;
using EpubFabric.Epub;

namespace EpubFabric.Tests;

public sealed class FixedLayoutXhtmlGeneratorTests
{
    [Fact]
    public void GeneratePage_GlyphRunWiderThanBox_GetsScaleXToFitLineWidth()
    {
        // 幅0.3×600=180px の枠に全角10文字。font-size=0.85×20.9=17.7px、自然幅≈177px…
        // に対し、枠より明確に広い/狭い場合に scaleX が付くことを確認するため、
        // 枠幅を自然幅より狭くする（全角10文字、枠120px → scaleX≈0.68）。
        var page = CreatePage(CreateBlock("p0001-b0001", new BoundingBox(0.1, 0.1, 0.2, 0.026), "科学は複数の人間と機器"));

        var xhtml = new FixedLayoutXhtmlGenerator().GeneratePage(page, "page-0001.png", "ja").ToString();

        Assert.Contains("transform:scaleX(0.", xhtml);
    }

    [Fact]
    public void GeneratePage_HalfWidthCharsCountAsHalfEm()
    {
        // 半角20文字 = 10em相当。全角10文字と同じ枠なら同程度のscaleXになる。
        var fullWidth = new FixedLayoutXhtmlGenerator()
            .GeneratePage(CreatePage(CreateBlock("p0001-b0001", new BoundingBox(0.1, 0.1, 0.2, 0.026), "科学は複数の人間と機")), "p.png", "ja")
            .ToString();
        var halfWidth = new FixedLayoutXhtmlGenerator()
            .GeneratePage(CreatePage(CreateBlock("p0001-b0001", new BoundingBox(0.1, 0.1, 0.2, 0.026), "Kagaku Fukusu 20char")), "p.png", "ja")
            .ToString();

        var scaleFull = ExtractScaleX(fullWidth);
        var scaleHalf = ExtractScaleX(halfWidth);
        Assert.Equal(scaleFull, scaleHalf, precision: 2);
    }

    [Fact]
    public void GeneratePage_BoxMatchingNaturalWidth_HasNoScaleX()
    {
        // 全角10文字 × font-size(0.85×15.6=13.26px) = 132.6px ≒ 枠幅132.6px → scaleX省略。
        var height = 0.026;
        var fontSize = height * 600 * 0.85; // pageHeight=600
        var widthRatio = fontSize * 10 / 400.0; // pageWidth=400
        var page = CreatePage(CreateBlock("p0001-b0001", new BoundingBox(0.1, 0.1, widthRatio, height), "科学は複数の人間と機"), pageWidth: 400, pageHeight: 600);

        var xhtml = new FixedLayoutXhtmlGenerator().GeneratePage(page, "page-0001.png", "ja").ToString();

        Assert.DoesNotContain("transform:scaleX", xhtml);
    }

    private static double ExtractScaleX(string xhtml)
    {
        var match = System.Text.RegularExpressions.Regex.Match(xhtml, @"transform:scaleX\(([\d.]+)\)");
        Assert.True(match.Success, "scaleXが出力されていない");
        return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static DocumentPage CreatePage(PageBlock block, int pageWidth = 600, int pageHeight = 600)
    {
        var page = new DocumentPage
        {
            PageNumber = 1,
            OriginalImagePath = "page-1.png",
            ProcessedImagePath = "page-1.png",
            PreviewImagePath = "page-1.png",
            Width = pageWidth,
            Height = pageHeight,
        };
        page.Blocks.Add(block);
        return page;
    }

    private static PageBlock CreateBlock(string id, BoundingBox bounds, string text) => new()
    {
        Id = id,
        PageNumber = 1,
        Bounds = bounds,
        Type = BlockType.Body,
        OcrText = text,
        TextSource = TextSourceKind.Ocr,
        ReadingOrder = 0,
    };
}
