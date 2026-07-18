using EpubFabric.Core.Models;
using OpenCvSharp;

namespace EpubFabric.Imaging;

/// <summary>
/// ページ画像から各テキスト行のインク密度（行ボックス内の黒画素率）を測定する。
/// 太字は同じ文字サイズでも黒画素率が本文より高くなるため、行高さでは検出できない
/// 太字見出しの分類材料になる（設計 0b(c) の「サイズが本文と同じゴシック見出し」対策）。
/// 二値化はページごとの大津法で行い、紙色の明暗差の影響を受けにくくする。
/// </summary>
public sealed class LineInkDensityMeasurer
{
    public IReadOnlyList<TextLine> Measure(string imagePath, IReadOnlyList<TextLine> lines)
    {
        if (lines.Count == 0)
        {
            return lines;
        }

        using var gray = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
        if (gray.Empty())
        {
            return lines;
        }

        using var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        var results = new List<TextLine>(lines.Count);
        foreach (var line in lines)
        {
            var x = (int)Math.Clamp(line.Bounds.X * gray.Width, 0, gray.Width - 1);
            var y = (int)Math.Clamp(line.Bounds.Y * gray.Height, 0, gray.Height - 1);
            var width = (int)Math.Clamp(line.Bounds.Width * gray.Width, 1, gray.Width - x);
            var height = (int)Math.Clamp(line.Bounds.Height * gray.Height, 1, gray.Height - y);

            using var region = new Mat(binary, new Rect(x, y, width, height));
            var density = Cv2.CountNonZero(region) / (double)(width * height);
            results.Add(line with { InkDensity = density });
        }

        return results;
    }
}
