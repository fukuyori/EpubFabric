using EpubFabric.Core.Models;
using OpenCvSharp;

namespace EpubFabric.Imaging;

/// <summary>
/// 9.4 レイアウト解析：OCRでは検出できない非テキスト領域（図・囲み記事の罫線）の候補を
/// 画像処理で推定する。既知のOCR行座標をテキスト領域として除外し、残った領域のうち
/// 絵柄（エッジ密度が高い塊）を図の候補、矩形の罫線を囲み記事の候補として検出する。
/// あくまで候補の検出であり、最終的な種別判定（図かコードか等）はOllama連携
/// （第3段階）または人手校正で行う想定。
/// </summary>
public sealed class NonTextRegionDetector
{
    private const double MinFigureAreaRatio = 0.015;
    private const double MaxFigureAreaRatio = 0.6;
    private const double MinBoxedAreaRatio = 0.02;
    private const double MaxBoxedAreaRatio = 0.7;

    public List<NonTextRegion> DetectRegions(string imagePath, IReadOnlyList<BoundingBox> textLineBounds)
    {
        using var gray = Cv2.ImRead(imagePath, ImreadModes.Grayscale);
        if (gray.Empty())
        {
            return [];
        }

        var width = gray.Width;
        var height = gray.Height;
        var pageArea = (double)width * height;

        var figureRegions = DetectFigureRegions(gray, textLineBounds, width, height, pageArea);
        var boxedRegions = DetectBoxedRegions(gray, width, height, pageArea)
            // 罫線・枠のある写真やスクリーンショットは、外枠が「中空の矩形」として
            // 誤って囲み記事候補に検出されることがある。図として検出済みの領域と
            // 大きく重なるものは除外する（図の判定を優先する）。
            .Where(boxed => !figureRegions.Any(figure => OverlapsSignificantly(boxed.Bounds, figure.Bounds)))
            .ToList();

        var regions = new List<NonTextRegion>();
        regions.AddRange(figureRegions);
        regions.AddRange(boxedRegions);
        return regions;
    }

    private static bool OverlapsSignificantly(BoundingBox a, BoundingBox b)
    {
        var overlapX = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X));
        var overlapY = Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        var overlapArea = overlapX * overlapY;
        var smallerArea = Math.Min(a.Width * a.Height, b.Width * b.Height);

        return smallerArea > 0 && overlapArea / smallerArea > 0.5;
    }

    /// <summary>
    /// OCR行に覆われていない領域のうち、エッジ密度が高い（写真・イラストらしい）塊を図の候補とする。
    /// 単なる余白はエッジがほとんど無いため候補にならない。
    /// </summary>
    private static List<NonTextRegion> DetectFigureRegions(
        Mat gray, IReadOnlyList<BoundingBox> textLineBounds, int width, int height, double pageArea)
    {
        using var textMask = new Mat(gray.Size(), MatType.CV_8UC1, Scalar.All(0));
        foreach (var box in textLineBounds)
        {
            Cv2.Rectangle(textMask, ToPixelRect(box, width, height), Scalar.All(255), thickness: -1);
        }

        using var edges = new Mat();
        Cv2.Canny(gray, edges, 60, 160);

        using var invertedTextMask = new Mat();
        Cv2.BitwiseNot(textMask, invertedTextMask);

        using var nonTextEdges = new Mat();
        Cv2.BitwiseAnd(edges, invertedTextMask, nonTextEdges);

        using var dilated = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(25, 25));
        Cv2.Dilate(nonTextEdges, dilated, kernel);

        Cv2.FindContours(dilated, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        var regions = new List<NonTextRegion>();
        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);
            var ratio = (double)rect.Width * rect.Height / pageArea;
            if (ratio is < MinFigureAreaRatio or > MaxFigureAreaRatio)
            {
                continue;
            }

            // 極端に細長い領域（罫線・傷など）は図として扱わない。
            var aspect = (double)rect.Width / rect.Height;
            if (aspect is > 8 or < 0.125)
            {
                continue;
            }

            regions.Add(new NonTextRegion(ToBoundingBox(rect, width, height), NonTextRegionKind.Figure));
        }

        return regions;
    }

    /// <summary>
    /// 4頂点に近似できる閉じた輪郭で、輪郭面積が外接矩形面積の大半を占めるもの
    /// （＝塗りつぶしではなく矩形の枠線）を囲み記事の候補とする。
    /// </summary>
    private static List<NonTextRegion> DetectBoxedRegions(Mat gray, int width, int height, double pageArea)
    {
        using var binary = new Mat();
        Cv2.Threshold(gray, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        Cv2.BitwiseNot(binary, binary); // 罫線・文字（インク）を白、背景を黒にする。

        // RETR_CCOMPで外側輪郭と穴（内側輪郭）の階層を取得する。単なる文字の塊は内部に
        // 大きな穴を持たないが、矩形の罫線（枠）は内側がくり抜かれた穴として検出される。
        // この「穴の有無・大きさ」で、文字の塊のシルエットがたまたま矩形に近似される
        // 誤検出（例：太いロゴ文字）を除外する。
        Cv2.FindContours(binary, out var contours, out var hierarchy, RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);

        var regions = new List<NonTextRegion>();
        for (var i = 0; i < contours.Length; i++)
        {
            var childIndex = hierarchy[i].Child;
            if (childIndex < 0)
            {
                continue; // 内側に穴が無い＝中身が詰まった塊であり、枠線ではない。
            }

            var contour = contours[i];
            var perimeter = Cv2.ArcLength(contour, closed: true);
            var approx = Cv2.ApproxPolyDP(contour, 0.02 * perimeter, closed: true);
            if (approx.Length != 4)
            {
                continue;
            }

            var rect = Cv2.BoundingRect(approx);
            var area = (double)rect.Width * rect.Height;
            var ratio = area / pageArea;
            if (ratio is < MinBoxedAreaRatio or > MaxBoxedAreaRatio)
            {
                continue;
            }

            // 最大の穴が外側輪郭の大半を占める＝薄い罫線で囲まれた中空の矩形。
            var outerArea = Cv2.ContourArea(contour);
            var largestHoleArea = MaxHoleArea(contours, hierarchy, childIndex);
            if (outerArea <= 0 || largestHoleArea / outerArea < 0.6)
            {
                continue;
            }

            regions.Add(new NonTextRegion(ToBoundingBox(rect, width, height), NonTextRegionKind.Boxed));
        }

        return regions;
    }

    private static double MaxHoleArea(Point[][] contours, HierarchyIndex[] hierarchy, int firstChildIndex)
    {
        var maxArea = 0.0;
        var index = firstChildIndex;
        while (index >= 0)
        {
            maxArea = Math.Max(maxArea, Cv2.ContourArea(contours[index]));
            index = hierarchy[index].Next;
        }

        return maxArea;
    }

    private static Rect ToPixelRect(BoundingBox box, int width, int height) => new(
        (int)(box.X * width),
        (int)(box.Y * height),
        Math.Max(1, (int)(box.Width * width)),
        Math.Max(1, (int)(box.Height * height)));

    private static BoundingBox ToBoundingBox(Rect rect, int width, int height) => new(
        (double)rect.X / width,
        (double)rect.Y / height,
        (double)rect.Width / width,
        (double)rect.Height / height);
}
