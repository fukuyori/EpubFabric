using EpubFabric.Core.Models;
using EpubFabric.Imaging;
using OpenCvSharp;

namespace EpubFabric.Tests;

public sealed class OcrImagePreprocessorTests : IDisposable
{
    private readonly string _tempDirectory =
        Directory.CreateTempSubdirectory("epubfabric-ocr-preprocess-").FullName;

    public void Dispose() => Directory.Delete(_tempDirectory, recursive: true);

    [Fact]
    public void 傾いたページを推定して補正する()
    {
        const double skewDegrees = 2.5;
        var originalPath = Path.Combine(_tempDirectory, "skewed.png");
        var processedPath = Path.Combine(_tempDirectory, "processed.png");
        CreateTextLikeImage(originalPath, skewDegrees);

        var result = new OcrImagePreprocessor().Preprocess(originalPath, processedPath);

        Assert.True(result.DeskewApplied);
        // 作成時に+2.5°回転させたので、補正角はおおむね-2.5°になる。
        Assert.InRange(result.SkewAngleDegrees, -skewDegrees - 0.4, -skewDegrees + 0.4);
        Assert.True(File.Exists(processedPath));
        Assert.Equal(processedPath, result.ImagePathForOcr);
    }

    [Fact]
    public void 傾きのないページは補正せず元画像を使う()
    {
        var originalPath = Path.Combine(_tempDirectory, "straight.png");
        var processedPath = Path.Combine(_tempDirectory, "processed.png");
        CreateTextLikeImage(originalPath, 0.0);

        var result = new OcrImagePreprocessor().Preprocess(originalPath, processedPath);

        Assert.False(result.DeskewApplied);
        Assert.Equal(originalPath, result.ImagePathForOcr);
        Assert.False(File.Exists(processedPath));
    }

    /// <summary>
    /// 写真が主体で本文行のないページでは、被写体の輪郭が投影の山を作り、
    /// まったく傾いていない紙面に極端な角度が出る（実データの裏表紙で-10.5°を観測）。
    /// 行が揃ったことによる山かどうかを確かめて棄却することの回帰。
    /// </summary>
    [Fact]
    public void 写真主体のページは傾きを推定しない()
    {
        var originalPath = Path.Combine(_tempDirectory, "photo.png");
        var processedPath = Path.Combine(_tempDirectory, "processed.png");

        using (var page = new Mat(1600, 1200, MatType.CV_8UC3, Scalar.All(255)))
        {
            // 斜めに置かれた被写体を模した楕円と、その影。行構造は持たない。
            Cv2.Ellipse(page, new Point(600, 500), new Size(320, 180), 35, 0, 360, Scalar.All(40), thickness: -1);
            Cv2.Ellipse(page, new Point(500, 1050), new Size(280, 150), -25, 0, 360, Scalar.All(90), thickness: -1);
            Cv2.Ellipse(page, new Point(850, 1300), new Size(200, 120), 15, 0, 360, Scalar.All(120), thickness: -1);
            Cv2.ImWrite(originalPath, page);
        }

        var result = new OcrImagePreprocessor().Preprocess(originalPath, processedPath);

        Assert.False(result.DeskewApplied);
        Assert.Equal(originalPath, result.ImagePathForOcr);
    }

    [Fact]
    public void 白紙ページは補正しない()
    {
        var originalPath = Path.Combine(_tempDirectory, "blank.png");
        var processedPath = Path.Combine(_tempDirectory, "processed.png");
        using (var blank = new Mat(800, 600, MatType.CV_8UC3, Scalar.All(255)))
        {
            Cv2.ImWrite(originalPath, blank);
        }

        var result = new OcrImagePreprocessor().Preprocess(originalPath, processedPath);

        Assert.False(result.DeskewApplied);
    }

    [Fact]
    public void 補正なしの場合は座標が変わらない()
    {
        var result = new OcrPreprocessResult("dummy.png", DeskewApplied: false, 0.0, 1000, 1400);
        var bounds = new BoundingBox(0.2, 0.3, 0.4, 0.05);

        Assert.Equal(bounds, result.MapToOriginal(bounds));
    }

    [Fact]
    public void 補正後座標の逆変換は画像中心を動かさない()
    {
        var result = new OcrPreprocessResult("dummy.png", DeskewApplied: true, -3.0, 1000, 1400);
        var centered = new BoundingBox(0.45, 0.45, 0.1, 0.1);

        var mapped = result.MapToOriginal(centered);

        // 中心対称の矩形は回転しても中心が変わらず、外接矩形はわずかに広がるだけ。
        Assert.InRange(mapped.X + mapped.Width / 2, 0.499, 0.501);
        Assert.InRange(mapped.Y + mapped.Height / 2, 0.499, 0.501);
        Assert.True(mapped.Width >= centered.Width);
        Assert.True(mapped.Height >= centered.Height);
    }

    [Fact]
    public void 補正後座標の逆変換は補正前画像の文字位置に一致する()
    {
        const double skewDegrees = 3.0;
        var originalPath = Path.Combine(_tempDirectory, "skewed-box.png");
        var processedPath = Path.Combine(_tempDirectory, "processed-box.png");

        // 本文行のある紙面を+3°回転した状態を「スキャン原稿」とする。
        // 単独の図形だけの紙面では傾き推定が「行が揃った」と判断できず補正されない
        // （写真ページの誤補正を防ぐ判定が働く）ため、行のある紙面を使う。
        const int width = 1200;
        const int height = 1600;
        CreateTextLikeImage(originalPath, skewDegrees);

        var result = new OcrImagePreprocessor().Preprocess(originalPath, processedPath);
        Assert.True(result.DeskewApplied);

        // 補正後画像で黒矩形を検出し、その正規化座標を逆変換すると、
        // 元画像（=スキャン原稿）上の黒矩形の位置と一致するはず。
        Rect detectedInProcessed;
        using (var processed = Cv2.ImRead(processedPath, ImreadModes.Grayscale))
        using (var binary = new Mat())
        {
            Cv2.Threshold(processed, binary, 128, 255, ThresholdTypes.BinaryInv);
            detectedInProcessed = Cv2.BoundingRect(binary);
        }

        var processedBounds = new BoundingBox(
            (double)detectedInProcessed.X / width,
            (double)detectedInProcessed.Y / height,
            (double)detectedInProcessed.Width / width,
            (double)detectedInProcessed.Height / height);

        var mapped = result.MapToOriginal(processedBounds);

        Rect actualInOriginal;
        using (var original = Cv2.ImRead(originalPath, ImreadModes.Grayscale))
        using (var binary = new Mat())
        {
            Cv2.Threshold(original, binary, 128, 255, ThresholdTypes.BinaryInv);
            actualInOriginal = Cv2.BoundingRect(binary);
        }

        Assert.InRange(mapped.X * width, actualInOriginal.X - 10, actualInOriginal.X + 10);
        Assert.InRange(mapped.Y * height, actualInOriginal.Y - 10, actualInOriginal.Y + 10);
        Assert.InRange((mapped.X + mapped.Width) * width, actualInOriginal.Right - 10, actualInOriginal.Right + 10);
        Assert.InRange((mapped.Y + mapped.Height) * height, actualInOriginal.Bottom - 10, actualInOriginal.Bottom + 10);
    }

    /// <summary>本文らしい横縞（テキスト行）を持つページ画像を、指定角度だけ傾けて保存する。</summary>
    private static void CreateTextLikeImage(string path, double skewDegrees)
    {
        const int width = 1200;
        const int height = 1600;
        using var page = new Mat(height, width, MatType.CV_8UC3, Scalar.All(255));

        for (var y = 150; y < height - 150; y += 60)
        {
            Cv2.Rectangle(page, new Rect(120, y, width - 240, 28), Scalar.All(30), thickness: -1);
        }

        if (Math.Abs(skewDegrees) < 1e-9)
        {
            Cv2.ImWrite(path, page);
            return;
        }

        using var matrix = Cv2.GetRotationMatrix2D(new Point2f(width / 2f, height / 2f), skewDegrees, 1.0);
        using var skewed = new Mat();
        Cv2.WarpAffine(page, skewed, matrix, page.Size(), InterpolationFlags.Cubic, BorderTypes.Constant, Scalar.All(255));
        Cv2.ImWrite(path, skewed);
    }
}
