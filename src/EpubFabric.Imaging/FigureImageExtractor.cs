using EpubFabric.Core.Models;
using OpenCvSharp;

namespace EpubFabric.Imaging;

/// <summary>
/// 7.4 画像処理「画像領域の切り出し」：検出した領域をページ画像から切り出して保存する。
/// </summary>
public sealed class FigureImageExtractor
{
    public void Extract(string sourceImagePath, BoundingBox bounds, string outputPath)
    {
        using var source = Cv2.ImRead(sourceImagePath, ImreadModes.Color);
        if (source.Empty())
        {
            throw new InvalidOperationException($"画像を読み込めませんでした: {sourceImagePath}");
        }

        var rect = new Rect(
            (int)(bounds.X * source.Width),
            (int)(bounds.Y * source.Height),
            Math.Max(1, (int)(bounds.Width * source.Width)),
            Math.Max(1, (int)(bounds.Height * source.Height)));

        // 丸め誤差でページ範囲をわずかに超えることがあるため、境界内に収める。
        rect = rect.Intersect(new Rect(0, 0, source.Width, source.Height));

        using var cropped = new Mat(source, rect);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Cv2.ImWrite(outputPath, cropped);
    }
}
