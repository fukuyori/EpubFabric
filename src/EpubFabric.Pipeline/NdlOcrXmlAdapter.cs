using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using EpubFabric.Core.Models;

namespace EpubFabric.Pipeline;

public sealed record NdlOcrPageResult(
    IReadOnlyList<TextLine> Lines,
    int ImageWidth,
    int ImageHeight)
{
    public double AverageConfidence => Lines.Count == 0 ? 0 : Lines.Average(line => line.Confidence);

    public double VerticalLineShare => Lines.Count == 0
        ? 0
        : (double)Lines.Count(line => line.DetectedWritingMode == WritingMode.Vertical) / Lines.Count;
}

/// <summary>NDLOCR-Lite XMLをEpubFabricの正規化済みTextLineへ変換する。</summary>
public static class NdlOcrXmlAdapter
{
    public static NdlOcrPageResult Parse(string xmlPath)
    {
        using var stream = File.OpenRead(xmlPath);
        return Parse(stream);
    }

    public static NdlOcrPageResult Parse(Stream xmlStream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };
        using var reader = XmlReader.Create(xmlStream, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var page = document.Root?.Element("PAGE")
            ?? throw new InvalidDataException("NDLOCR-Lite XMLにPAGE要素がありません。");

        var imageWidth = RequiredPositiveInt(page, "WIDTH");
        var imageHeight = RequiredPositiveInt(page, "HEIGHT");
        var lines = new List<TextLine>();
        var orders = new HashSet<int>();

        foreach (var element in page.Descendants("LINE"))
        {
            var text = (string?)element.Attribute("STRING") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var order = RequiredNonNegativeInt(element, "ORDER");
            if (!orders.Add(order))
            {
                throw new InvalidDataException($"NDLOCR-Lite XMLのORDERが重複しています: {order}");
            }

            var x = RequiredNonNegativeInt(element, "X");
            var y = RequiredNonNegativeInt(element, "Y");
            var width = RequiredPositiveInt(element, "WIDTH");
            var height = RequiredPositiveInt(element, "HEIGHT");
            var confidence = OptionalDouble(element, "CONF", 0);
            var bounds = NormalizeBounds(x, y, width, height, imageWidth, imageHeight);
            var writingMode = height > width ? WritingMode.Vertical : WritingMode.Horizontal;

            lines.Add(new TextLine(
                bounds,
                text,
                Math.Clamp(confidence, 0, 1),
                TextSourceKind.NdlOcr,
                DetectedWritingMode: writingMode,
                SourceReadingOrder: order,
                SourceType: (string?)element.Attribute("TYPE")));
        }

        if (lines.Count == 0)
        {
            throw new InvalidDataException("NDLOCR-Lite XMLに有効な文字行がありません。");
        }

        return new NdlOcrPageResult(
            lines.OrderBy(line => line.SourceReadingOrder).ToList(),
            imageWidth,
            imageHeight);
    }

    private static BoundingBox NormalizeBounds(
        int x,
        int y,
        int width,
        int height,
        int imageWidth,
        int imageHeight)
    {
        var left = Math.Clamp((double)x / imageWidth, 0, 1);
        var top = Math.Clamp((double)y / imageHeight, 0, 1);
        var right = Math.Clamp((double)(x + width) / imageWidth, left, 1);
        var bottom = Math.Clamp((double)(y + height) / imageHeight, top, 1);
        if (right <= left || bottom <= top)
        {
            throw new InvalidDataException("NDLOCR-Lite XMLにページ外または空の文字行座標があります。");
        }

        return new BoundingBox(left, top, right - left, bottom - top);
    }

    private static int RequiredPositiveInt(XElement element, string attributeName)
    {
        var value = RequiredInt(element, attributeName);
        return value > 0
            ? value
            : throw new InvalidDataException($"NDLOCR-Lite XMLの{attributeName}は正の整数である必要があります。");
    }

    private static int RequiredNonNegativeInt(XElement element, string attributeName)
    {
        var value = RequiredInt(element, attributeName);
        return value >= 0
            ? value
            : throw new InvalidDataException($"NDLOCR-Lite XMLの{attributeName}は0以上である必要があります。");
    }

    private static int RequiredInt(XElement element, string attributeName) =>
        int.TryParse((string?)element.Attribute(attributeName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException($"NDLOCR-Lite XMLの{attributeName}を整数として読めません。");

    private static double OptionalDouble(XElement element, string attributeName, double fallback) =>
        double.TryParse((string?)element.Attribute(attributeName), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
