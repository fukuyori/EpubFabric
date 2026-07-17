using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using EpubFabric.Core.Models;
using OpenCvSharp;

namespace EpubFabric.Pdf;

/// <summary>
/// 9.1 PDF読み込みと9.2 ページ画像化を担当する。
/// </summary>
public sealed class PdfDocumentService
{
    private readonly IDocLib _docLib = DocLib.Instance;

    public PdfDocumentInfo GetInfo(string pdfPath, string? password = null)
    {
        using var reader = OpenReader(pdfPath, password, dpi: 72);

        var pageCount = reader.GetPageCount();
        var pages = new List<PdfPageInfo>(pageCount);
        var hasTextLayer = false;

        for (var i = 0; i < pageCount; i++)
        {
            using var page = reader.GetPageReader(i);
            var pageHasText = !string.IsNullOrWhiteSpace(page.GetText());
            hasTextLayer |= pageHasText;

            pages.Add(new PdfPageInfo(
                PageNumber: i + 1,
                WidthPoints: page.GetPageWidth(),
                HeightPoints: page.GetPageHeight(),
                HasText: pageHasText));
        }

        return new PdfDocumentInfo(reader.GetPdfVersion().ToString() ?? "unknown", pageCount, hasTextLayer, pages);
    }

    public string ExtractPageText(string pdfPath, int pageNumber, string? password = null)
    {
        using var reader = OpenReader(pdfPath, password, dpi: 72);
        using var page = reader.GetPageReader(pageNumber - 1);
        return page.GetText();
    }

    /// <summary>
    /// テキストレイヤーの文字座標から行単位のTextLineを再構成する（9.3）。
    /// OCR結果と同じ形で返すため、テキスト層PDFにもレイアウト解析
    /// （見出し検出・段組み判定・柱除去）を適用できる。
    /// 座標は0～1のページ比率に正規化する。文字座標を取得できない場合は空を返す。
    /// </summary>
    public IReadOnlyList<TextLine> ExtractTextLines(string pdfPath, int pageNumber, string? password = null)
    {
        using var reader = OpenReader(pdfPath, password, dpi: 72);
        using var page = reader.GetPageReader(pageNumber - 1);

        double pageWidth = page.GetPageWidth();
        double pageHeight = page.GetPageHeight();
        if (pageWidth <= 0 || pageHeight <= 0)
        {
            return [];
        }

        var characters = page.GetCharacters()
            .Where(c => !char.IsWhiteSpace(c.Char) && c.Box.Right > c.Box.Left && c.Box.Bottom > c.Box.Top)
            .ToList();
        if (characters.Count == 0)
        {
            return [];
        }

        var lines = new List<TextLine>();

        foreach (var cluster in ClusterIntoLines(characters))
        {
            lines.AddRange(SplitClusterIntoSegments(cluster, pageWidth, pageHeight));
        }

        return lines
            .OrderBy(l => l.Bounds.Y)
            .ThenBy(l => l.Bounds.X)
            .ToList();
    }

    /// <summary>縦方向に重なる文字同士を同じ行としてまとめる。</summary>
    private static List<List<Character>> ClusterIntoLines(List<Character> characters)
    {
        var clusters = new List<(double Top, double Bottom, List<Character> Chars)>();

        foreach (var ch in characters.OrderBy(c => c.Box.Top).ThenBy(c => c.Box.Left))
        {
            var height = (double)(ch.Box.Bottom - ch.Box.Top);
            var assigned = false;

            for (var i = clusters.Count - 1; i >= 0; i--)
            {
                var (top, bottom, chars) = clusters[i];
                var overlap = Math.Min(bottom, ch.Box.Bottom) - Math.Max(top, ch.Box.Top);
                if (overlap > 0.5 * Math.Min(height, bottom - top))
                {
                    chars.Add(ch);
                    clusters[i] = (Math.Min(top, ch.Box.Top), Math.Max(bottom, ch.Box.Bottom), chars);
                    assigned = true;
                    break;
                }

                // 文字はTop順に処理しているため、これより上の行に入ることはない。
                if (bottom < ch.Box.Top)
                {
                    break;
                }
            }

            if (!assigned)
            {
                clusters.Add((ch.Box.Top, ch.Box.Bottom, [ch]));
            }
        }

        return clusters.Select(c => c.Chars).ToList();
    }

    /// <summary>
    /// 同じ高さにある文字列を、大きな水平間隔（段組みのガター等）で分割し、
    /// 単語間隔程度の間隔にはスペースを補って1行のテキストにする。
    /// </summary>
    private static IEnumerable<TextLine> SplitClusterIntoSegments(List<Character> cluster, double pageWidth, double pageHeight)
    {
        var ordered = cluster.OrderBy(c => c.Box.Left).ToList();
        var heights = ordered.Select(c => (double)(c.Box.Bottom - c.Box.Top)).OrderBy(h => h).ToList();
        var medianHeight = heights[heights.Count / 2];
        var gutterGap = Math.Max(1.5 * medianHeight, 0.02 * pageWidth);

        var segment = new List<Character>();

        foreach (var ch in ordered)
        {
            if (segment.Count > 0 && ch.Box.Left - segment[^1].Box.Right > gutterGap)
            {
                yield return BuildLine(segment, medianHeight, pageWidth, pageHeight);
                segment = [];
            }

            segment.Add(ch);
        }

        if (segment.Count > 0)
        {
            yield return BuildLine(segment, medianHeight, pageWidth, pageHeight);
        }
    }

    private static TextLine BuildLine(List<Character> segment, double medianHeight, double pageWidth, double pageHeight)
    {
        // 和文主体の行はスペース復元を行わない。和文の単語間にスペースは入らず、
        // 和文中の欧文・数字は全角風の広い送りで組まれることが多く、隙間の大きさでは
        // 単語間と区別できないため（例:「2000年」「cat」が誤分割される）。
        var cjkCount = segment.Count(c => IsCjk(c.Char));
        var restoreSpaces = cjkCount < 0.2 * segment.Count;
        var wordGapThreshold = restoreSpaces ? EstimateWordGapThreshold(segment, medianHeight) : 0;

        var text = new System.Text.StringBuilder(segment.Count + 8);

        for (var i = 0; i < segment.Count; i++)
        {
            // 空白文字は除外済みのため、欧文の単語間隔をスペースで復元する。
            // 数字同士の隙間は桁組みの可能性が高いため対象外とする。
            if (restoreSpaces
                && i > 0
                && !IsCjk(segment[i - 1].Char)
                && !IsCjk(segment[i].Char)
                && !(char.IsAsciiDigit(segment[i - 1].Char) && char.IsAsciiDigit(segment[i].Char))
                && segment[i].Box.Left - segment[i - 1].Box.Right > wordGapThreshold)
            {
                text.Append(' ');
            }

            text.Append(segment[i].Char);
        }

        double left = segment.Min(c => c.Box.Left);
        double top = segment.Min(c => c.Box.Top);
        double right = segment.Max(c => c.Box.Right);
        double bottom = segment.Max(c => c.Box.Bottom);

        return new TextLine(
            new BoundingBox(
                left / pageWidth,
                top / pageHeight,
                (right - left) / pageWidth,
                (bottom - top) / pageHeight),
            text.ToString(),
            Confidence: 1.0);
    }

    /// <summary>
    /// 単語間とみなす隙間のしきい値を行ごとに推定する。座標はグリフのインクボックスで、
    /// 字間の隙間はフォントにより大きくばらつくため、固定のem比だけでは「字間で誤分割」
    /// と「単語間を見逃す」の両方が起きる。行内の欧文字間の中央値を基準にした適応値と
    /// em基準の上限を併用する。
    /// </summary>
    private static double EstimateWordGapThreshold(List<Character> segment, double medianHeight)
    {
        var fontSizes = segment.Where(c => c.FontSize > 0).Select(c => c.FontSize).OrderBy(v => v).ToList();
        var em = fontSizes.Count > 0 ? fontSizes[fontSizes.Count / 2] : medianHeight;

        var gaps = new List<double>();
        for (var i = 1; i < segment.Count; i++)
        {
            if (IsCjk(segment[i - 1].Char) || IsCjk(segment[i].Char))
            {
                continue;
            }

            var gap = (double)(segment[i].Box.Left - segment[i - 1].Box.Right);
            if (gap > 0)
            {
                gaps.Add(gap);
            }
        }

        if (gaps.Count == 0)
        {
            return 0.28 * em;
        }

        gaps.Sort();
        var medianGap = gaps[gaps.Count / 2];
        return Math.Min(0.28 * em, Math.Max(0.12 * em, 1.8 * medianGap));
    }

    /// <summary>和文（CJK・全角記号・半角カナ）はスペース復元の対象外にする。</summary>
    private static bool IsCjk(char c) =>
        (c >= '⺀' && c <= '鿿')   // CJK部首・かな・CJK統合漢字
        || (c >= '豈' && c <= '﫿') // CJK互換漢字
        || (c >= '＀' && c <= '￯'); // 全角英数・全角記号・半角カナ

    /// <summary>
    /// ページを指定dpiでPNGへラスタライズする（page-original相当、8.2）。
    /// </summary>
    public void RenderPageToPng(string pdfPath, int pageNumber, string outputPath, int dpi = 300, string? password = null)
    {
        using var reader = OpenReader(pdfPath, password, dpi);
        using var page = reader.GetPageReader(pageNumber - 1);

        var width = page.GetPageWidth();
        var height = page.GetPageHeight();
        var bgra = page.GetImage();

        using var mat = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgra);

        // テキスト層のみのページは背景のアルファが0で、そのまま保存すると
        // アルファ非対応の処理（OpenCVのImRead等）で黒地に黒文字になる。
        // すべての利用側が不透明な画像を前提にできるよう、白地へ合成して保存する。
        using var flattened = FlattenOnWhite(mat);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Cv2.ImWrite(outputPath, flattened);
    }

    private static Mat FlattenOnWhite(Mat bgra)
    {
        var channels = Cv2.Split(bgra);
        try
        {
            using var alpha = new Mat();
            channels[3].ConvertTo(alpha, MatType.CV_32F, 1.0 / 255);

            using var ones = (Mat)Mat.Ones(alpha.Rows, alpha.Cols, MatType.CV_32FC1);
            using var inverseAlpha = new Mat();
            Cv2.Subtract(ones, alpha, inverseAlpha);
            using var background = new Mat();
            inverseAlpha.ConvertTo(background, MatType.CV_32F, 255.0);

            var blended = new Mat[3];
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    using var color = new Mat();
                    channels[i].ConvertTo(color, MatType.CV_32F);

                    // result = color * a + 255 * (1 - a)
                    using var foreground = new Mat();
                    Cv2.Multiply(color, alpha, foreground);
                    using var sum = new Mat();
                    Cv2.Add(foreground, background, sum);

                    blended[i] = new Mat();
                    sum.ConvertTo(blended[i], MatType.CV_8U);
                }

                var result = new Mat();
                Cv2.Merge(blended, result);
                return result;
            }
            finally
            {
                foreach (var channel in blended)
                {
                    channel?.Dispose();
                }
            }
        }
        finally
        {
            foreach (var channel in channels)
            {
                channel.Dispose();
            }
        }
    }

    private IDocReader OpenReader(string pdfPath, string? password, int dpi)
    {
        try
        {
            var scale = dpi / 72.0;
            var dimensions = new PageDimensions(scale);

            return password is null
                ? _docLib.GetDocReader(pdfPath, dimensions)
                : _docLib.GetDocReader(pdfPath, password, dimensions);
        }
        catch (Exception ex)
        {
            throw new PdfLoadException($"PDFを開けませんでした（暗号化されている可能性があります）: {pdfPath}", ex);
        }
    }
}
