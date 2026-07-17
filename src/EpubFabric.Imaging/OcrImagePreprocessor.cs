using EpubFabric.Core.Models;
using OpenCvSharp;

namespace EpubFabric.Imaging;

/// <summary>
/// OCR前処理の結果。傾き補正が適用された場合、OCRは補正後画像に対して行い、
/// 得られた座標は<see cref="MapToOriginal"/>で元画像の座標系へ戻す。
/// 表示・EPUB出力には常に元画像を使う（原型保証の設計原則）。
/// </summary>
public sealed record OcrPreprocessResult(
    string ImagePathForOcr,
    bool DeskewApplied,
    double SkewAngleDegrees,
    int Width,
    int Height)
{
    /// <summary>
    /// 補正後画像上の正規化座標を、元画像上の正規化座標へ逆変換する。
    /// 四隅を逆回転してから外接矩形を取り直す。
    /// </summary>
    public BoundingBox MapToOriginal(BoundingBox bounds)
    {
        if (!DeskewApplied)
        {
            return bounds;
        }

        var cx = Width / 2.0;
        var cy = Height / 2.0;

        // GetRotationMatrix2D(center, θ) の逆変換は R(-θ)。画像座標系（y下向き）では
        // 順変換が [cosθ sinθ; -sinθ cosθ] なので、逆変換は [cosθ -sinθ; sinθ cosθ]。
        var rad = SkewAngleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);

        Span<double> xs = stackalloc double[4];
        Span<double> ys = stackalloc double[4];
        ReadOnlySpan<double> px = [bounds.X * Width, (bounds.X + bounds.Width) * Width, bounds.X * Width, (bounds.X + bounds.Width) * Width];
        ReadOnlySpan<double> py = [bounds.Y * Height, bounds.Y * Height, (bounds.Y + bounds.Height) * Height, (bounds.Y + bounds.Height) * Height];

        for (var i = 0; i < 4; i++)
        {
            var dx = px[i] - cx;
            var dy = py[i] - cy;
            xs[i] = cos * dx - sin * dy + cx;
            ys[i] = sin * dx + cos * dy + cy;
        }

        var minX = Math.Clamp(Min4(xs) / Width, 0.0, 1.0);
        var minY = Math.Clamp(Min4(ys) / Height, 0.0, 1.0);
        var maxX = Math.Clamp(Max4(xs) / Width, 0.0, 1.0);
        var maxY = Math.Clamp(Max4(ys) / Height, 0.0, 1.0);

        return new BoundingBox(minX, minY, maxX - minX, maxY - minY);
    }

    private static double Min4(ReadOnlySpan<double> v) => Math.Min(Math.Min(v[0], v[1]), Math.Min(v[2], v[3]));

    private static double Max4(ReadOnlySpan<double> v) => Math.Max(Math.Max(v[0], v[1]), Math.Max(v[2], v[3]));
}

/// <summary>
/// 9.3 OCR用画像前処理：スキャン原稿の傾きを推定し、OCR専用の補正済み画像を生成する。
/// 傾き推定は投影プロファイル法（行方向の画素和の分散が最大になる角度を探索）。
/// OCRmyPDFと同様、表示用画像には手を加えず、OCR入力だけを補正する。
/// </summary>
public sealed class OcrImagePreprocessor
{
    /// <summary>この角度未満の傾きは補正しない（補間による画質劣化の方が害になる）。</summary>
    private const double MinCorrectionDegrees = 0.2;

    /// <summary>探索する傾きの上限。これを超える傾きはページ回転の問題であり別処理。</summary>
    private const double MaxSearchDegrees = 10.0;

    /// <summary>傾き推定に使う縮小画像の高さ。精度0.1°の探索にはこの程度で十分。</summary>
    private const int AnalysisHeight = 800;

    public OcrPreprocessResult Preprocess(string originalImagePath, string processedImagePath)
    {
        using var original = Cv2.ImRead(originalImagePath, ImreadModes.Color);
        if (original.Empty())
        {
            throw new ArgumentException($"画像を読み込めません: {originalImagePath}");
        }

        var angle = EstimateSkewAngle(original);
        if (Math.Abs(angle) < MinCorrectionDegrees)
        {
            return new OcrPreprocessResult(originalImagePath, false, 0.0, original.Width, original.Height);
        }

        using var rotated = Rotate(original, angle, Scalar.All(255));
        Cv2.ImWrite(processedImagePath, rotated);
        return new OcrPreprocessResult(processedImagePath, true, angle, original.Width, original.Height);
    }

    private static double EstimateSkewAngle(Mat original)
    {
        using var gray = new Mat();
        Cv2.CvtColor(original, gray, ColorConversionCodes.BGR2GRAY);

        var scale = Math.Min(1.0, (double)AnalysisHeight / gray.Height);
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(Math.Max(1, (int)(gray.Width * scale)), Math.Max(1, (int)(gray.Height * scale))));

        using var binary = new Mat();
        Cv2.Threshold(small, binary, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        // ほぼ白紙のページでノイズから無意味な角度を推定しない。
        if (Cv2.CountNonZero(binary) < binary.Total() * 0.001)
        {
            return 0.0;
        }

        var coarse = SearchBestAngle(binary, -MaxSearchDegrees, MaxSearchDegrees, 0.5);
        return SearchBestAngle(binary, coarse - 0.5, coarse + 0.5, 0.1);
    }

    private static double SearchBestAngle(Mat binary, double from, double to, double step)
    {
        var bestAngle = 0.0;
        var bestScore = double.MinValue;

        for (var angle = from; angle <= to + 1e-9; angle += step)
        {
            var score = ProjectionScore(binary, angle);
            if (score > bestScore)
            {
                bestScore = score;
                bestAngle = angle;
            }
        }

        return bestAngle;
    }

    /// <summary>
    /// 指定角度で回転したときの行方向投影の標準偏差。テキスト行と行間が水平に揃うほど
    /// 濃い行と空白の行のコントラストが大きくなり、値が大きくなる。
    /// </summary>
    private static double ProjectionScore(Mat binary, double angleDegrees)
    {
        using var rotated = Rotate(binary, angleDegrees, Scalar.All(0));
        using var rowSums = new Mat();
        Cv2.Reduce(rotated, rowSums, ReduceDimension.Column, ReduceTypes.Sum, MatType.CV_32F);
        Cv2.MeanStdDev(rowSums, out _, out var stddev);
        return stddev.Val0;
    }

    private static Mat Rotate(Mat source, double angleDegrees, Scalar fill)
    {
        var center = new Point2f(source.Width / 2f, source.Height / 2f);
        using var matrix = Cv2.GetRotationMatrix2D(center, angleDegrees, 1.0);
        var rotated = new Mat();
        Cv2.WarpAffine(source, rotated, matrix, source.Size(), InterpolationFlags.Cubic, BorderTypes.Constant, fill);
        return rotated;
    }
}
