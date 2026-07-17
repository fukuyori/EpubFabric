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

        var image = new XElement(
            Xhtml + "img",
            new XAttribute("class", "page-image"),
            new XAttribute("src", $"../images/{imageFileName}"),
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

            textLayer.Add(CreatePositionedText(block, text, pageWidth, pageHeight));
        }

        var pageContainer = new XElement(
            Xhtml + "section",
            new XAttribute("class", "page-container"),
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
                    new XAttribute("content", $"width={pageWidth},height={pageHeight}")),
                new XElement(
                    Xhtml + "link",
                    new XAttribute("rel", "stylesheet"),
                    new XAttribute("type", "text/css"),
                    new XAttribute("href", "../styles/fixed-layout.css"))),
            new XElement(Xhtml + "body", pageContainer));

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), html);
    }

    private static XElement CreatePositionedText(PageBlock block, string text, int pageWidth, int pageHeight)
    {
        var left = ClampRatio(block.Bounds.X) * pageWidth;
        var top = ClampRatio(block.Bounds.Y) * pageHeight;
        var width = Math.Max(1, ClampRatio(block.Bounds.Width) * pageWidth);
        var height = Math.Max(1, ClampRatio(block.Bounds.Height) * pageHeight);
        var fontSize = Math.Max(1, height * 0.85);

        var style = string.Join(
            ";",
            $"left:{Number(left)}px",
            $"top:{Number(top)}px",
            $"width:{Number(width)}px",
            $"height:{Number(height)}px",
            $"font-size:{Number(fontSize)}px");

        return new XElement(
            Xhtml + "span",
            new XAttribute("id", block.Id),
            new XAttribute("class", $"positioned-text block-{block.Type.ToString().ToLowerInvariant()}"),
            new XAttribute("style", style),
            text);
    }

    private static double ClampRatio(double value) => Math.Clamp(value, 0, 1);

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
