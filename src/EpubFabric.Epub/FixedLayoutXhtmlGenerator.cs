using System.Globalization;
using System.Xml.Linq;
using EpubFabric.Core.Models;

namespace EpubFabric.Epub;

/// <summary>
/// 1つのPDFページから、ページ画像と座標付き透明テキスト層を持つ固定レイアウトXHTMLを生成する。
/// </summary>
public sealed class FixedLayoutXhtmlGenerator
{
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";

    public XDocument GeneratePage(DocumentPage page, string imageFileName, string language)
    {
        var pageWidth = Math.Max(1, page.Width);
        var pageHeight = Math.Max(1, page.Height);
        var canvasStyle = $"width:{pageWidth}px;height:{pageHeight}px";

        var image = new XElement(
            Xhtml + "img",
            new XAttribute("class", "page-image"),
            new XAttribute("src", $"../images/{imageFileName}"),
            new XAttribute("width", pageWidth),
            new XAttribute("height", pageHeight),
            new XAttribute("alt", string.Empty),
            new XAttribute("role", "presentation"));

        var textLayer = new XElement(Xhtml + "div", new XAttribute("class", "text-layer"));

        foreach (var block in page.Blocks
            .Where(b => !b.IsExcluded)
            .OrderBy(b => b.ReadingOrder)
            .ThenBy(b => b.Bounds.Y)
            .ThenBy(b => b.Bounds.X))
        {
            var text = XmlTextSanitizer.Sanitize(block.CorrectedText ?? block.OcrText);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            textLayer.Add(CreatePositionedText(block, text, pageWidth, pageHeight, page.WritingMode));
        }

        var pageContainer = new XElement(
            Xhtml + "section",
            new XAttribute("class", "page-container"),
            new XAttribute("style", canvasStyle),
            new XAttribute("aria-label", $"Page {page.PageNumber}"),
            image,
            textLayer);

        var html = new XElement(
            Xhtml + "html",
            new XAttribute(XNamespace.Xml + "lang", language),
            new XElement(
                Xhtml + "head",
                new XElement(Xhtml + "title", $"Page {page.PageNumber}"),
                new XElement(
                    Xhtml + "meta",
                    new XAttribute("name", "viewport"),
                    new XAttribute("content", $"width={pageWidth}, height={pageHeight}")),
                new XElement(
                    Xhtml + "link",
                    new XAttribute("rel", "stylesheet"),
                    new XAttribute("type", "text/css"),
                    new XAttribute("href", "../styles/fixed-layout.css"))),
            new XElement(
                Xhtml + "body",
                new XAttribute("style", canvasStyle),
                pageContainer));

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), html);
    }

    private static XElement CreatePositionedText(PageBlock block, string text, int pageWidth, int pageHeight, WritingMode writingMode)
    {
        var left = ClampRatio(block.Bounds.X) * pageWidth;
        var top = ClampRatio(block.Bounds.Y) * pageHeight;
        var width = Math.Max(1, ClampRatio(block.Bounds.Width) * pageWidth);
        var height = Math.Max(1, ClampRatio(block.Bounds.Height) * pageHeight);

        // 縦書きページでも、キャプション・ノンブル等の横書き行が混在するため、
        // ブロック単位に縦長（高さが幅の1.5倍超）かどうかで縦書き描画を決める。
        var isVerticalLine = writingMode == WritingMode.Vertical && height > width * 1.5;
        var fontSize = Math.Max(1, (isVerticalLine ? width : height) * 0.85);

        var styleParts = new List<string>
        {
            $"left:{Number(left)}px",
            $"top:{Number(top)}px",
            $"width:{Number(width)}px",
            $"height:{Number(height)}px",
            $"font-size:{Number(fontSize)}px",
        };

        if (isVerticalLine)
        {
            styleParts.Add("writing-mode:vertical-rl");
        }

        // グリフ列の描画長はリーダーのフォント次第で「文字数×font-size」前後になり、
        // 紙面上の実際の行の長さとずれる（行の後半ほど選択・検索ハイライトがはみ出す）。
        // OCRmyPDFのTz（水平スケール）と同じ発想で、推定自然長→枠長のスケールを掛けて
        // 行の先頭と末尾の両方を紙面に合わせる。縦書き行は行方向が縦なのでscaleYを使う。
        var naturalLength = EstimateNaturalTextWidth(text) * fontSize;
        if (naturalLength > 0)
        {
            var boxLength = isVerticalLine ? height : width;
            var scale = Math.Clamp(boxLength / naturalLength, 0.5, 2.0);
            if (Math.Abs(scale - 1.0) > 0.02)
            {
                styleParts.Add(isVerticalLine
                    ? $"transform:scaleY({Number(scale)})"
                    : $"transform:scaleX({Number(scale)})");
            }
        }

        return new XElement(
            Xhtml + "span",
            new XAttribute("id", block.Id),
            new XAttribute("class", $"positioned-text block-{block.Type.ToString().ToLowerInvariant()}"),
            new XAttribute("data-text-source", TextSourceName(block.TextSource)),
            new XAttribute("style", string.Join(";", styleParts)),
            text);
    }

    /// <summary>
    /// テキストの自然描画幅をem単位で見積もる。全角（CJK・かな・全角記号）は1em、
    /// 半角（ASCII・半角カナ）は0.5emとする近似。正確なフォントメトリクスは
    /// リーダーごとに異なるため、scaleXの分母として使える程度の精度でよい。
    /// </summary>
    private static double EstimateNaturalTextWidth(string text)
    {
        var width = 0.0;
        foreach (var ch in text)
        {
            width += IsFullWidth(ch) ? 1.0 : 0.5;
        }

        return width;
    }

    private static bool IsFullWidth(char c) =>
        c is (>= 'ᄀ' and <= 'ᅟ')   // ハングル字母
            or (>= '⺀' and <= '꓏') // CJK部首・かな・CJK統合漢字・拡張A
            or (>= '가' and <= '힣') // ハングル音節
            or (>= '豈' and <= '﫿') // CJK互換漢字
            or (>= '︰' and <= '﹏') // CJK互換形
            or (>= '＀' and <= '｠') // 全角英数・記号
            or (>= '￠' and <= '￦'); // 全角記号

    private static double ClampRatio(double value) => Math.Clamp(value, 0, 1);

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string TextSourceName(TextSourceKind source) => source switch
    {
        TextSourceKind.PdfTextLayer => "pdf",
        TextSourceKind.Ocr => "ocr",
        _ => "unknown",
    };
}
