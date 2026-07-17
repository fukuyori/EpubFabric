using EpubFabric.Core.Models;

namespace EpubFabric.Layout;

/// <summary>
/// 9.4 レイアウト解析・9.6 仮読み順の簡易版。学習済みモデルを使わず、OCR/テキスト抽出で
/// 得た行単位の座標（0～1のページ比率）だけを手がかりに、見出し・段組み・柱／ノンブルを
/// 推定する。ここでの分類・読み順はあくまで仮のものであり、Ollama連携や人手校正（9.7・9.9）
/// による補正を前提とする。
/// </summary>
public sealed class HeuristicLayoutAnalyzer
{
    // 本文行の高さに対する倍率のしきい値。しきい値は利用者が変更できるようにする方針
    // （14章）に合わせ、将来は設定値化する。
    private const double ChapterTitleRatio = 1.8;
    private const double SectionHeadingRatio = 1.35;
    private const double SubheadingRatio = 1.15;

    private const double MarginBand = 0.06; // 上下6%を柱・ノンブルの候補域とする。
    private const int RunningTextMaxLength = 30;

    public List<PageBlock> AnalyzePage(int pageNumber, IReadOnlyList<TextLine> lines)
    {
        if (lines.Count == 0)
        {
            return [];
        }

        var bodyHeight = EstimateBodyLineHeight(lines);
        var columns = DetectColumns(lines);

        var blocks = new List<PageBlock>(lines.Count);
        var readingOrder = 0;

        foreach (var column in columns)
        {
            foreach (var line in column.OrderBy(l => l.Bounds.Y))
            {
                var type = ClassifyLine(line, bodyHeight);
                var isExcluded = type is BlockType.Header or BlockType.Footer or BlockType.PageNumber;

                blocks.Add(new PageBlock
                {
                    Id = $"p{pageNumber:0000}-b{blocks.Count + 1:0000}",
                    PageNumber = pageNumber,
                    Bounds = line.Bounds,
                    Type = type,
                    OcrText = line.Text,
                    OcrConfidence = line.Confidence,
                    ReadingOrder = readingOrder++,
                    HeadingLevel = HeadingLevelFor(type),
                    IsExcluded = isExcluded,
                    RequiresReview = line.Confidence < 0.85,
                });
            }
        }

        return blocks;
    }

    /// <summary>
    /// 本文の行高さを中央値で推定する。中央値は少数の見出し・柱による外れ値の影響を
    /// 受けにくく、フォントサイズ推定の基準として単純平均より頑健である。
    /// </summary>
    private static double EstimateBodyLineHeight(IReadOnlyList<TextLine> lines)
    {
        var heights = lines.Select(l => l.Bounds.Height).OrderBy(h => h).ToList();
        return heights[heights.Count / 2];
    }

    private static BlockType ClassifyLine(TextLine line, double bodyHeight)
    {
        var isNearTopMargin = line.Bounds.Y < MarginBand;
        var isNearBottomMargin = line.Bounds.Y + line.Bounds.Height > 1 - MarginBand;
        var isShort = line.Text.Length <= RunningTextMaxLength;

        if (isNearBottomMargin && isShort && line.Text.All(char.IsDigit))
        {
            return BlockType.PageNumber;
        }

        if (isNearTopMargin && isShort)
        {
            return BlockType.Header;
        }

        if (isNearBottomMargin && isShort)
        {
            return BlockType.Footer;
        }

        var ratio = bodyHeight > 0 ? line.Bounds.Height / bodyHeight : 1.0;

        return ratio switch
        {
            >= ChapterTitleRatio => BlockType.ChapterTitle,
            >= SectionHeadingRatio => BlockType.SectionHeading,
            >= SubheadingRatio => BlockType.Subheading,
            _ => BlockType.Body,
        };
    }

    private static int? HeadingLevelFor(BlockType type) => type switch
    {
        BlockType.ChapterTitle => 1,
        BlockType.SectionHeading => 2,
        BlockType.Subheading => 3,
        _ => null,
    };

    /// <summary>
    /// ページ中央付近に、どの行も跨がない縦の隙間（ガター）があれば2段組みとみなし、
    /// 左段・右段に分割する。隙間が見つからない場合は1段組みとして扱う。
    /// </summary>
    private static List<List<TextLine>> DetectColumns(IReadOnlyList<TextLine> lines)
    {
        const double gutterSearchStart = 0.40;
        const double gutterSearchEnd = 0.60;
        const double gutterStep = 0.01;
        const double minColumnShare = 0.2;

        for (var gutterX = gutterSearchStart; gutterX <= gutterSearchEnd; gutterX += gutterStep)
        {
            var crossesGutter = lines.Any(l => l.Bounds.X < gutterX && l.Bounds.X + l.Bounds.Width > gutterX);
            if (crossesGutter)
            {
                continue;
            }

            var left = lines.Where(l => l.Bounds.X + l.Bounds.Width / 2 < gutterX).ToList();
            var right = lines.Where(l => l.Bounds.X + l.Bounds.Width / 2 >= gutterX).ToList();

            if (left.Count >= lines.Count * minColumnShare && right.Count >= lines.Count * minColumnShare)
            {
                return [left, right];
            }
        }

        return [[.. lines]];
    }
}
