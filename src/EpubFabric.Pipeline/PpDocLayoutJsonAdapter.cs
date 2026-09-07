using System.Text.Json;
using EpubFabric.Core.Models;

namespace EpubFabric.Pipeline;

public sealed record PpDocLayoutRegion(
    BoundingBox Bounds,
    string Label,
    double Confidence,
    int? ReadingOrder);

/// <summary>PP-DocLayoutV2のJSONをページ比率座標の領域候補へ変換する。</summary>
public static class PpDocLayoutJsonAdapter
{
    public static IReadOnlyList<PpDocLayoutRegion> Parse(string jsonPath, int imageWidth, int imageHeight)
    {
        using var stream = File.OpenRead(jsonPath);
        return Parse(stream, imageWidth, imageHeight);
    }

    public static IReadOnlyList<PpDocLayoutRegion> Parse(Stream jsonStream, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "画像寸法は正の値である必要があります。");
        }

        using var document = JsonDocument.Parse(jsonStream);
        if (!document.RootElement.TryGetProperty("boxes", out var boxes) || boxes.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("PP-DocLayoutV2 JSONにboxes配列がありません。");
        }

        var regions = new List<PpDocLayoutRegion>();
        foreach (var box in boxes.EnumerateArray())
        {
            if (box.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("PP-DocLayoutV2 JSONのboxes要素がオブジェクトではありません。");
            }

            var label = RequiredProperty(box, "label", JsonValueKind.String).GetString();
            if (string.IsNullOrWhiteSpace(label))
            {
                throw new InvalidDataException("PP-DocLayoutV2 JSONに空のlabelがあります。");
            }

            var coordinateElement = RequiredProperty(box, "coordinate", JsonValueKind.Array);
            var coordinates = coordinateElement
                .EnumerateArray()
                .Select(value => value.ValueKind == JsonValueKind.Number
                    ? value.GetDouble()
                    : throw new InvalidDataException("PP-DocLayoutV2 JSONのcoordinateに数値以外があります。"))
                .ToArray();
            if (coordinates.Length != 4)
            {
                throw new InvalidDataException("PP-DocLayoutV2 JSONのcoordinateは4要素である必要があります。");
            }

            var left = Math.Clamp(coordinates[0] / imageWidth, 0, 1);
            var top = Math.Clamp(coordinates[1] / imageHeight, 0, 1);
            var right = Math.Clamp(coordinates[2] / imageWidth, left, 1);
            var bottom = Math.Clamp(coordinates[3] / imageHeight, top, 1);
            if (right <= left || bottom <= top)
            {
                throw new InvalidDataException("PP-DocLayoutV2 JSONにページ外または空の領域座標があります。");
            }

            var score = RequiredProperty(box, "score", JsonValueKind.Number).GetDouble();
            if (score is < 0 or > 1)
            {
                throw new InvalidDataException("PP-DocLayoutV2 JSONのscoreは0以上1以下である必要があります。");
            }

            int? order = null;
            if (box.TryGetProperty("order", out var orderElement)
                && orderElement.ValueKind != JsonValueKind.Null)
            {
                if (orderElement.ValueKind != JsonValueKind.Number
                    || !orderElement.TryGetInt32(out var parsedOrder)
                    || parsedOrder < 0)
                {
                    throw new InvalidDataException("PP-DocLayoutV2 JSONのorderはnullまたは0以上の整数である必要があります。");
                }

                order = parsedOrder;
            }

            regions.Add(new PpDocLayoutRegion(
                new BoundingBox(left, top, right - left, bottom - top),
                label,
                score,
                order));
        }

        return regions;
    }

    private static JsonElement RequiredProperty(
        JsonElement element,
        string propertyName,
        JsonValueKind expectedKind)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != expectedKind)
        {
            throw new InvalidDataException(
                $"PP-DocLayoutV2 JSONの{propertyName}がないか、型が正しくありません。");
        }

        return value;
    }
}
