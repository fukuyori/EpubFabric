using OpenCvSharp;

namespace EpubFabric.Imaging;

public sealed record PageEnhanceResult(
    string ImagePath,
    bool Applied,
    double PaperLuminance,
    double InkLuminance);

/// <summary>1ページから測った紙色の統計。書籍全体の補正値を決めるために集める。</summary>
public sealed record PageEnhanceStats(
    double PaperLuminance,
    double PaperShare,
    double PaperBlue,
    double PaperGreen,
    double PaperRed);

/// <summary>書籍全体で共通に使う紙色。ページごとの推定ぶれを持ち込まないための代表値。</summary>
public sealed record PageEnhanceProfile(
    double PaperLuminance,
    double PaperBlue,
    double PaperGreen,
    double PaperRed);

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

    /// <summary>白化の対象とする彩度の上限（HSVのS、0〜255）。色の付いた画素は地色ではない。</summary>
    private const double WhitenMaxSaturation = 55;

    /// <summary>白化の対象とする、紙色からの色味のずれの上限。
    /// 明るさの差ではなくチャンネル間の差（色対比）で測るため、単に紙より暗いだけの
    /// 画素（裏写り・地色ムラ）は対象に残り、色の付いた淡い図版だけが外れる。</summary>
    private const double WhitenMaxChromaDistance = 24;

    /// <summary>書籍全体の紙色を決める際、中央絶対偏差の何倍まで外れ値としないか。</summary>
    private const double PaperOutlierMadFactor = 1.5;

    /// <summary>書籍全体の紙色を決めるのに必要な最小ページ数。これ未満はページごとの推定に任せる。</summary>
    private const int MinPagesForProfile = 3;

    /// <summary>ページから紙色の統計だけを測る（画像は書き出さない）。</summary>
    public PageEnhanceStats Analyze(string originalImagePath)
    {
        using var bgr = ReadImage(originalImagePath);
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        return MeasurePaper(bgr, gray);
    }

    /// <summary>
    /// ページごとの紙色統計から、書籍全体で使う代表値を決める。
    /// 口絵・全面写真のように紙色を正しく測れないページが混じると代表値が歪むため、
    /// 中央絶対偏差（MAD）で輝度の外れ値ページを落としてから中央値を取る。
    /// 紙面と呼べるページが少ない場合はnullを返し、ページごとの推定に任せる。
    /// </summary>
    public static PageEnhanceProfile? BuildProfile(IReadOnlyList<PageEnhanceStats> stats)
    {
        var candidates = stats
            .Where(s => s.PaperLuminance >= MinPaperLuminance && s.PaperShare >= MinPaperShare)
            .ToList();

        if (candidates.Count < MinPagesForProfile)
        {
            return null;
        }

        var median = Median(candidates.Select(s => s.PaperLuminance));
        var deviation = Median(candidates.Select(s => Math.Abs(s.PaperLuminance - median)));

        // MADが0（紙色が揃っている）なら全ページを採用する。
        var inliers = deviation > 0
            ? candidates.Where(s => Math.Abs(s.PaperLuminance - median) <= deviation * PaperOutlierMadFactor).ToList()
            : candidates;

        if (inliers.Count < MinPagesForProfile)
        {
            inliers = candidates;
        }

        return new PageEnhanceProfile(
            Median(inliers.Select(s => s.PaperLuminance)),
            Median(inliers.Select(s => s.PaperBlue)),
            Median(inliers.Select(s => s.PaperGreen)),
            Median(inliers.Select(s => s.PaperRed)));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    private static Mat ReadImage(string path)
    {
        var bgr = Cv2.ImRead(path, ImreadModes.Color);
        if (bgr.Empty())
        {
            bgr.Dispose();
            throw new ArgumentException($"画像を読み込めません: {path}");
        }

        return bgr;
    }

    private static PageEnhanceStats MeasurePaper(Mat bgr, Mat gray)
    {
        var paperLuminance = EstimatePaperLuminance(gray);

        using var paperMask = new Mat();
        Cv2.Threshold(gray, paperMask, paperLuminance - PaperBandWidth, 255, ThresholdTypes.Binary);
        var paperShare = Cv2.CountNonZero(paperMask) / (double)gray.Total();
        var paperColor = Cv2.Mean(bgr, paperMask);

        return new PageEnhanceStats(paperLuminance, paperShare, paperColor.Val0, paperColor.Val1, paperColor.Val2);
    }

    /// <param name="profile">書籍全体の紙色。nullならこのページ単独の推定を使う。</param>
    public PageEnhanceResult Enhance(string originalImagePath, string enhancedImagePath, PageEnhanceProfile? profile = null)
    {
        using var bgr = ReadImage(originalImagePath);

        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);

        // 加工するかどうかはページ単独の統計で決める（写真ページは書籍の紙色に関係なく除外する）。
        var pageStats = MeasurePaper(bgr, gray);
        var paperLuminance = pageStats.PaperLuminance;
        var paperShare = pageStats.PaperShare;

        if (paperLuminance < MinPaperLuminance || paperShare < MinPaperShare)
        {
            return new PageEnhanceResult(originalImagePath, false, paperLuminance, 0);
        }

        // 補正の強さは書籍全体の紙色で決める。ページごとに決めると、写真の面積などで
        // 紙色の推定が揺れ、隣り合うページの明るさが揃わなくなる。
        var paperColor = profile is null
            ? new Scalar(pageStats.PaperBlue, pageStats.PaperGreen, pageStats.PaperRed)
            : new Scalar(profile.PaperBlue, profile.PaperGreen, profile.PaperRed);
        if (profile is not null)
        {
            paperLuminance = profile.PaperLuminance;
        }

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

        // 明るさだけで白化すると、淡い色の図版・色紙の地色まで白へ流れてしまう。
        // 「彩度が低く、かつ紙色に近い」画素だけを対象にする二重の条件で歯止めをかける。
        using var gate = BuildPaperGate(bgr, paperColor);
        Cv2.Multiply(smooth, gate, smooth);

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

    /// <summary>
    /// 白化してよい「地色の画素」を1、それ以外を0とするマスクを作る。
    /// 彩度が低いこと（色が付いていない）と、紙色に近いこと（別の淡い色ではない）の
    /// 両方を満たす画素だけを通す。
    /// </summary>
    private static Mat BuildPaperGate(Mat bgr, Scalar paperColor)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        var hsvChannels = Cv2.Split(hsv);

        using var bgr32 = new Mat();
        bgr.ConvertTo(bgr32, MatType.CV_32FC3);
        var channels = Cv2.Split(bgr32);

        try
        {
            using var saturation32 = new Mat();
            hsvChannels[1].ConvertTo(saturation32, MatType.CV_32FC1);
            using var lowSaturation = new Mat();
            Cv2.Threshold(saturation32, lowSaturation, WhitenMaxSaturation, 1.0, ThresholdTypes.BinaryInv);

            // 色味の差 = チャンネル間の差の、紙色からのずれ。明るさの差では動かない。
            using var blueMinusGreen = new Mat();
            Cv2.Subtract(channels[0], channels[1], blueMinusGreen);
            Cv2.Absdiff(blueMinusGreen, new Scalar(paperColor.Val0 - paperColor.Val1), blueMinusGreen);

            using var greenMinusRed = new Mat();
            Cv2.Subtract(channels[1], channels[2], greenMinusRed);
            Cv2.Absdiff(greenMinusRed, new Scalar(paperColor.Val1 - paperColor.Val2), greenMinusRed);

            using var chromaDistance = new Mat();
            Cv2.Add(blueMinusGreen, greenMinusRed, chromaDistance);

            using var nearPaperColor = new Mat();
            Cv2.Threshold(chromaDistance, nearPaperColor, WhitenMaxChromaDistance, 1.0, ThresholdTypes.BinaryInv);

            var gate = new Mat();
            Cv2.Multiply(lowSaturation, nearPaperColor, gate);
            return gate;
        }
        finally
        {
            foreach (var channel in hsvChannels.Concat(channels))
            {
                channel.Dispose();
            }
        }
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
