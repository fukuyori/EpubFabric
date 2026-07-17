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

        var columns = DetectColumns(items);

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

    /// <summary>
    /// ページ中央付近に、どの行も跨がない縦の隙間（ガター）があれば2段組みとみなし、
    /// 左段・右段に分割する。段をまたぐ幅広の項目（大きな図など）はガター判定の対象から
    /// 除外し、独立した1項目の「段」として扱った上で、Y座標に応じて他の段と並べ替える
    /// （段の途中への厳密な割り込みまでは行わない簡易処理）。隙間が見つからない場合は
    /// 全体を1段組みとして扱う。
    /// </summary>
    private static List<List<PositionedItem>> DetectColumns(IReadOnlyList<PositionedItem> items)
    {
        const double gutterSearchStart = 0.40;
        const double gutterSearchEnd = 0.60;
        const double gutterStep = 0.01;
        const double minColumnShare = 0.2;
        const double maxNormalItemWidth = 0.5; // これより広い項目は最初から段をまたぐ要素として扱う。
        const int maxCrossingTolerance = 2; // 図のキャプションなど、例外的に段をまたぐ要素を許容する数。

        var normalItems = items.Where(i => i.Bounds.Width <= maxNormalItemWidth).ToList();
        var wideItems = items.Where(i => i.Bounds.Width > maxNormalItemWidth).ToList();

        List<List<PositionedItem>>? columns = null;

        for (var gutterX = gutterSearchStart; gutterX <= gutterSearchEnd; gutterX += gutterStep)
        {
            var crossing = normalItems.Where(i => i.Bounds.X < gutterX && i.Bounds.X + i.Bounds.Width > gutterX).ToList();
            if (crossing.Count > maxCrossingTolerance)
            {
                continue;
            }

            var nonCrossing = normalItems.Except(crossing).ToList();
            var left = nonCrossing.Where(i => i.Bounds.X + i.Bounds.Width / 2 < gutterX).ToList();
            var right = nonCrossing.Where(i => i.Bounds.X + i.Bounds.Width / 2 >= gutterX).ToList();

            if (left.Count >= nonCrossing.Count * minColumnShare && right.Count >= nonCrossing.Count * minColumnShare)
            {
                columns = [left, right];
                wideItems = [.. wideItems, .. crossing]; // 段をまたぐ例外行も、大きな図と同様に個別の段として扱う。
                break;
            }
        }

        if (columns is null)
        {
            // ガターが見つからない1段組みページでは、幅広の行も同じ段に属する
            // 通常の行であり、別扱いすると読み順が崩れるため全項目を1つの段にまとめる。
            return items.Count > 0 ? [items.OrderBy(i => i.Bounds.Y).ToList()] : [];
        }

        foreach (var wideItem in wideItems)
        {
            columns.Add([wideItem]);
        }

        return columns
            .Where(c => c.Count > 0)
            .OrderBy(c => c.Min(i => i.Bounds.Y))
            .ToList();
    }

    private readonly record struct PositionedItem(BoundingBox Bounds, TextLine? Line, NonTextRegion? Region);
}
