using OpenCvSharp;

namespace EpubFabric.Imaging;

public sealed record PageEnhanceResult(
    string ImagePath,
    bool Applied,
    double PaperLuminance,
    double InkLuminance);

/// <summary>
/// スキャン紙面の高品質化（DN_SuperBook_PDF_Converterの手法を参考にした独自実装）。
/// 輝度ヒストグラムから紙色（背景）を推定し、
/// (1) 紙色を白へ寄せるチャンネル別ホワイトバランス正規化（黄ばみ・くすみの除去）、
/// (2) 紙に近い明るさの画素をスムーズステップで白へ寄せる裏写り・地色ムラの抑制、を行う。
/// インク側への黒点ストレッチは行わない。下位パーセンタイルによるインク推定は
/// グレー図版を「インク」と誤認して中間調（写真・網点）を黒潰れさせるため、
/// ページ単独の統計では安全に決められない。
/// 幾何変換を含まないため、ページ上の座標（透明テキスト層・ブロック枠）には影響しない。
/// 紙面の大半が紙でないページ（表紙・全面写真）は誤って洗い流さないよう無加工で返す。
/// </summary>
public sealed class PageImageEnhancer
{
    /// <summary>紙色の推定に使う輝度ヒストグラムの上側パーセンタイル。</summary>
    private const double PaperPercentile = 0.95;

    /// <summary>推定紙色がこれより暗いページは写真・表紙とみなして加工しない。</summary>
    private const double MinPaperLuminance = 176;

    /// <summary>紙とみなす輝度幅（推定紙輝度からの下方向の許容差）。</summary>
    private const double PaperBandWidth = 16;

    /// <summary>ページに占める紙画素の最低割合。これ未満は紙面ではない（表紙・全面写真）。</summary>
    private const double MinPaperShare = 0.25;

    /// <summary>ホワイトバランスの倍率上限。写真のハイライトを飛ばしすぎない範囲。</summary>
    private const double MaxWhiteBalanceScale = 1.4;

    /// <summary>正規化後、この輝度から白化を始める（スムーズステップの下端）。</summary>
    private const double WhitenStart = 222;

    /// <summary>この輝度以上は完全な白にする（スムーズステップの上端）。</summary>
    private const double WhitenEnd = 247;

    public PageEnhanceResult Enhance(string originalImagePath, string enhancedImagePath)
    {
        using var bgr = Cv2.ImRead(originalImagePath, ImreadModes.Color);
        if (bgr.Empty())
        {
            throw new ArgumentException($"画像を読み込めません: {originalImagePath}");
        }

        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);

        var paperLuminance = EstimatePaperLuminance(gray);

        using var paperMask = new Mat();
        Cv2.Threshold(gray, paperMask, paperLuminance - PaperBandWidth, 255, ThresholdTypes.Binary);
        var paperShare = Cv2.CountNonZero(paperMask) / (double)gray.Total();

        if (paperLuminance < MinPaperLuminance || paperShare < MinPaperShare)
        {
            return new PageEnhanceResult(originalImagePath, false, paperLuminance, 0);
        }

        // 紙画素の平均色から、チャンネル別に「紙→白」のホワイトバランス倍率を決める。
        var paperColor = Cv2.Mean(bgr, paperMask);
        var scaleB = WhiteBalanceScale(paperColor.Val0);
        var scaleG = WhiteBalanceScale(paperColor.Val1);
        var scaleR = WhiteBalanceScale(paperColor.Val2);

        using var balanced = new Mat();
        bgr.ConvertTo(balanced, MatType.CV_32FC3);
        Cv2.Multiply(balanced, new Scalar(scaleB, scaleG, scaleR), balanced);

        // 裏写り抑制: 正規化後の輝度が紙に近い画素を、スムーズステップの重みで白へ混ぜる。
        // しきい値を高めに取ることで、写真・図版の中間調は白化の対象にならない。
        var luminanceScale = Math.Clamp(255.0 / paperLuminance, 1.0, MaxWhiteBalanceScale);
        using var weight = new Mat();
        gray.ConvertTo(weight, MatType.CV_32FC1, luminanceScale / (WhitenEnd - WhitenStart), -WhitenStart / (WhitenEnd - WhitenStart));
        Cv2.Min(weight, new Scalar(1.0), weight);
        Cv2.Max(weight, new Scalar(0.0), weight);

        // smoothstep: s = w^2 * (3 - 2w)
        using var threeMinusTwoW = new Mat();
        weight.ConvertTo(threeMinusTwoW, MatType.CV_32FC1, -2.0, 3.0);
        using var weightSquared = new Mat();
        Cv2.Multiply(weight, weight, weightSquared);
        using var smooth = new Mat();
        Cv2.Multiply(weightSquared, threeMinusTwoW, smooth);

        using var smooth3 = new Mat();
        Cv2.Merge([smooth, smooth, smooth], smooth3);

        // result = balanced * (1 - s) + 255 * s
        using var inverse = new Mat();
        smooth3.ConvertTo(inverse, MatType.CV_32FC3, -1.0, 1.0);
        using var keptPart = new Mat();
        Cv2.Multiply(balanced, inverse, keptPart);
        using var whitePart = new Mat();
        smooth3.ConvertTo(whitePart, MatType.CV_32FC3, 255.0);
        using var blended = new Mat();
        Cv2.Add(keptPart, whitePart, blended);

        using var result = new Mat();
        blended.ConvertTo(result, MatType.CV_8UC3);
        Cv2.ImWrite(enhancedImagePath, result);

        return new PageEnhanceResult(enhancedImagePath, true, paperLuminance, 0);
    }

    private static double WhiteBalanceScale(double paperChannelValue) =>
        Math.Clamp(255.0 / Math.Max(paperChannelValue, 1.0), 1.0, MaxWhiteBalanceScale);

    /// <summary>輝度ヒストグラムの累積分布から、紙（上側95%点）の代表輝度を求める。</summary>
    private static double EstimatePaperLuminance(Mat gray)
    {
        using var histogram = new Mat();
        Cv2.CalcHist([gray], [0], null, histogram, 1, [256], [new Rangef(0, 256)]);

        var total = (double)gray.Total();
        var cumulative = 0.0;

        for (var i = 0; i < 256; i++)
        {
            cumulative += histogram.At<float>(i);
            if (cumulative / total >= PaperPercentile)
            {
                return i;
            }
        }

        return 255;
    }
}
