using EpubFabric.Core.Models;
using OpenCvSharp;

namespace EpubFabric.Imaging;

/// <summary>
/// 評価レポート用に、ページ画像へ検出ブロックの枠（種別ごとの色）と
/// 読み順番号を描き込んだオーバーレイ画像を生成する。
/// </summary>
public sealed class BlockOverlayRenderer
{
    /// <summary>レポートが数百ページ分の画像を持つため、この幅まで縮小して容量を抑える。</summary>
    private const int MaxOutputWidth = 1200;

    public void Render(string sourceImagePath, IReadOnlyList<PageBlock> blocks, string outputPath)
    {
        using var source = Cv2.ImRead(sourceImagePath, ImreadModes.Color);
        if (source.Empty())
        {
            throw new InvalidOperationException($"画像を読み込めませんでした: {sourceImagePath}");
        }

        using var canvas = ResizeToMaxWidth(source);

        foreach (var block in blocks.OrderBy(b => b.ReadingOrder))
        {
            var rect = new Rect(
                (int)(block.Bounds.X * canvas.Width),
                (int)(block.Bounds.Y * canvas.Height),
                Math.Max(1, (int)(block.Bounds.Width * canvas.Width)),
                Math.Max(1, (int)(block.Bounds.Height * canvas.Height)));
            rect = rect.Intersect(new Rect(0, 0, canvas.Width, canvas.Height));

            var color = ToScalar(BlockTypeColors.HexFor(block.Type));
            var thickness = block.IsExcluded ? 1 : 2;
            Cv2.Rectangle(canvas, rect, color, thickness);

            var label = $"{block.ReadingOrder} {block.Type}{(block.IsExcluded ? " (excluded)" : "")}";
            DrawLabel(canvas, rect, label, color);
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Cv2.ImWrite(outputPath, canvas);
    }

    private static Mat ResizeToMaxWidth(Mat source)
    {
        if (source.Width <= MaxOutputWidth)
        {
            return source.Clone();
        }

        var scale = (double)MaxOutputWidth / source.Width;
        var resized = new Mat();
        Cv2.Resize(source, resized, new Size(MaxOutputWidth, (int)(source.Height * scale)), interpolation: InterpolationFlags.Area);
        return resized;
    }

    private static void DrawLabel(Mat canvas, Rect blockRect, string label, Scalar color)
    {
        const double fontScale = 0.45;
        const int fontThickness = 1;
        var textSize = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, fontScale, fontThickness, out var baseline);

        // ラベルは枠の左上の内側に置き、ページ外へはみ出さないようにする。
        var origin = new Point(
            Math.Clamp(blockRect.X, 0, Math.Max(0, canvas.Width - textSize.Width)),
            Math.Max(textSize.Height + 2, blockRect.Y + textSize.Height + 2));

        var background = new Rect(
            origin.X,
            origin.Y - textSize.Height - 2,
            Math.Min(textSize.Width + 4, canvas.Width - origin.X),
            textSize.Height + baseline + 2);
        Cv2.Rectangle(canvas, background, color, thickness: -1);
        Cv2.PutText(canvas, label, new Point(origin.X + 2, origin.Y), HersheyFonts.HersheySimplex, fontScale, Scalar.White, fontThickness, LineTypes.AntiAlias);
    }

    private static Scalar ToScalar(string hex)
    {
        var r = Convert.ToInt32(hex.Substring(1, 2), 16);
        var g = Convert.ToInt32(hex.Substring(3, 2), 16);
        var b = Convert.ToInt32(hex.Substring(5, 2), 16);
        return new Scalar(b, g, r);
    }
}
