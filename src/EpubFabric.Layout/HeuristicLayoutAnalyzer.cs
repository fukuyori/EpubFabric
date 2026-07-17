using EpubFabric.Core.Models;

namespace EpubFabric.Layout;

/// <summary>
/// 9.4 レイアウト解析・9.6 仮読み順の簡易版。学習済みモデルを使わず、OCR/テキスト抽出で
/// 得た行単位の座標と、画像処理（EpubFabric.Imaging）で検出した非テキスト領域の候補
/// （0～1のページ比率）だけを手がかりに、見出し・段組み・柱／ノンブル・図・囲み記事・
/// キャプションを推定する。ここでの分類・読み順はあくまで仮のものであり、Ollama連携や
/// 人手校正（9.7・9.9）による補正を前提とする。
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
    private const double MaxCaptionGap = 0.03; // 図の下端からキャプション候補行までの最大距離。

    public List<PageBlock> AnalyzePage(
        int pageNumber,
        IReadOnlyList<TextLine> lines,
        IReadOnlyList<NonTextRegion>? regions = null)
    {
        regions ??= [];
        var figureRegions = regions.Where(r => r.Kind == NonTextRegionKind.Figure).ToList();
        var boxedRegions = regions.Where(r => r.Kind == NonTextRegionKind.Boxed).ToList();

        // 図の内部にあるOCR行（図中のラベル等）は、切り出した図画像に既に含まれているため、
        // 別の本文段落として重複させない。
        var effectiveLines = lines.Where(l => !figureRegions.Any(f => OverlapsSignificantly(l.Bounds, f.Bounds))).ToList();

        if (effectiveLines.Count == 0 && figureRegions.Count == 0)
        {
            return [];
        }

        var bodyHeight = effectiveLines.Count > 0 ? EstimateBodyLineHeight(effectiveLines) : 0;

        var items = new List<PositionedItem>(effectiveLines.Count + figureRegions.Count);
        items.AddRange(effectiveLines.Select(l => new PositionedItem(l.Bounds, l, null)));
        items.AddRange(figureRegions.Select(r => new PositionedItem(r.Bounds, null, r)));

        var columns = ColumnDetector.DetectColumns(items, i => i.Bounds);

        var blocks = new List<PageBlock>(items.Count);
        var figureBlocks = new List<PageBlock>();
        var readingOrder = 0;

        foreach (var column in columns)
        {
            foreach (var item in column.OrderBy(i => i.Bounds.Y))
            {
                var block = item.Region is { } figureRegion
                    ? CreateFigureBlock(pageNumber, blocks.Count + 1, figureRegion, readingOrder++)
                    : CreateTextBlock(pageNumber, blocks.Count + 1, item.Line!, bodyHeight, boxedRegions, readingOrder++);

                if (block.Type == BlockType.Figure)
                {
                    figureBlocks.Add(block);
                }

                blocks.Add(block);
            }
        }

        LinkCaptions(blocks, figureBlocks);

        return blocks;
    }

    private static PageBlock CreateFigureBlock(int pageNumber, int blockIndex, NonTextRegion region, int readingOrder) => new()
    {
        Id = $"p{pageNumber:0000}-b{blockIndex:0000}",
        PageNumber = pageNumber,
        Bounds = region.Bounds,
        Type = BlockType.Figure,
        OcrText = string.Empty,
        ReadingOrder = readingOrder,
    };

    private static PageBlock CreateTextBlock(
        int pageNumber, int blockIndex, TextLine line, double bodyHeight, IReadOnlyList<NonTextRegion> boxedRegions, int readingOrder)
    {
        var isBoxed = boxedRegions.Any(b => OverlapsSignificantly(line.Bounds, b.Bounds));
        var type = isBoxed ? BlockType.Aside : ClassifyLine(line, bodyHeight);
        var isExcluded = type is BlockType.Header or BlockType.Footer or BlockType.PageNumber;

        return new PageBlock
        {
            Id = $"p{pageNumber:0000}-b{blockIndex:0000}",
            PageNumber = pageNumber,
            Bounds = line.Bounds,
            Type = type,
            OcrText = line.Text,
            OcrConfidence = line.Confidence,
            TextSource = line.Source,
            ReadingOrder = readingOrder,
            HeadingLevel = HeadingLevelFor(type),
            IsExcluded = isExcluded,
            RequiresReview = line.Confidence < 0.85,
        };
    }

    /// <summary>
    /// 図の直下にあり、水平方向に重なる本文行をキャプションとして関連付ける
    /// （PageBlock.RelatedBlockId、design 9.7「画像とキャプションの関連付け」の簡易版）。
    /// キャプション内の行間隔と、同じ段内で後に続く本文の行間隔はOCR座標だけでは
    /// 区別できないため、行数の上限（MaxCaptionLines）で歯止めをかける。上限を超えて
    /// 貪欲に取り込むと、キャプションに続く本文まで際限なく取り込んでしまうため。
    /// </summary>
    private static void LinkCaptions(List<PageBlock> blocks, IReadOnlyList<PageBlock> figureBlocks)
    {
        const int maxCaptionLines = 3;

        foreach (var figure in figureBlocks)
        {
            var figureIndex = blocks.IndexOf(figure);
            var bottom = figure.Bounds.Y + figure.Bounds.Height;
            var linkedCount = 0;

            for (var i = figureIndex + 1; i < blocks.Count && linkedCount < maxCaptionLines; i++)
            {
                var candidate = blocks[i];
                if (candidate.Type != BlockType.Body)
                {
                    break;
                }

                var gap = candidate.Bounds.Y - bottom;
                var horizontallyAligned = candidate.Bounds.X < figure.Bounds.X + figure.Bounds.Width
                    && candidate.Bounds.X + candidate.Bounds.Width > figure.Bounds.X;

                if (gap is < 0 or > MaxCaptionGap || !horizontallyAligned)
                {
                    break;
                }

                candidate.Type = BlockType.Caption;
                candidate.RelatedBlockId = figure.Id;
                bottom = candidate.Bounds.Y + candidate.Bounds.Height;
                linkedCount++;
            }
        }
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

    private static bool OverlapsSignificantly(BoundingBox line, BoundingBox box)
    {
        var overlapX = Math.Max(0, Math.Min(line.X + line.Width, box.X + box.Width) - Math.Max(line.X, box.X));
        var overlapY = Math.Max(0, Math.Min(line.Y + line.Height, box.Y + box.Height) - Math.Max(line.Y, box.Y));
        var overlapArea = overlapX * overlapY;
        var lineArea = line.Width * line.Height;

        return lineArea > 0 && overlapArea / lineArea > 0.5;
    }

    private readonly record struct PositionedItem(BoundingBox Bounds, TextLine? Line, NonTextRegion? Region);
}
