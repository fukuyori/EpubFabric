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

    /// <summary>本文中央値に対するインク密度の倍率がこれ以上の短い行は太字見出し（小見出し）とみなす。
    /// 高さが本文と同じゴシック太字見出しは高さ基準では検出できないため（0b(c)）。</summary>
    private const double BoldSubheadingDensityRatio = 1.4;

    /// <summary>この倍率を超える密度は太字ではなく罫線・図形が行ボックスに重なった可能性が高いので見出し扱いしない。</summary>
    private const double BoldSubheadingDensityCeiling = 3.0;

    /// <summary>太字見出し候補の最小文字数（空白除く）。句読点だけの断片行を除外する。</summary>
    private const int BoldSubheadingMinLength = 4;

    /// <summary>章タイトルとみなす行のページ内の最大Y位置。章の大見出しはページ上部にある。</summary>
    private const double ChapterTitleMaxTop = 0.4;

    /// <summary>章タイトルの最小文字数（空白除く）。挿絵内の1〜2文字の巨大文字を除外する。</summary>
    private const int ChapterTitleMinLength = 3;

    private const double MarginBand = 0.06; // 上下6%を柱・ノンブルの候補域とする。
    private const int RunningTextMaxLength = 30;
    private const double MaxCaptionGap = 0.03; // 図の下端からキャプション候補行までの最大距離。
    private const double FootnoteBottomFraction = 0.66;
    private const double FootnoteMaxHeightRatio = 0.85;

    public List<PageBlock> AnalyzePage(
        int pageNumber,
        IReadOnlyList<TextLine> lines,
        IReadOnlyList<NonTextRegion>? regions = null,
        WritingMode writingMode = WritingMode.Horizontal,
        PageLayoutProfile? layoutProfile = null)
    {
        regions ??= [];

        // 縦書きはページを時計回りに90度回した仮想座標へ移すと、縦の文字列が横書きの
        // 行になり、右から左への行順も上から下の順になる。分類・段検出・キャプション
        // 関連付けはこの座標で行い、返却前に元のページ座標へ戻す。
        if (writingMode == WritingMode.Vertical)
        {
            lines = lines
                .Select(line => line with { Bounds = ToReadingCoordinates(line.Bounds) })
                .ToList();
            regions = regions
                .Select(region => region with { Bounds = ToReadingCoordinates(region.Bounds) })
                .ToList();
        }

        var boxedRegions = regions.Where(r => r.Kind == NonTextRegionKind.Boxed).ToList();
        var preliminaryBodyHeight = lines.Count > 0 ? EstimateBodyLineHeight(lines) : 0;
        var preservedFigureHeadings = new HashSet<TextLine>();

        // 図として検出された領域でも、内部がテキスト行で覆われていてコード的な内容なら
        // 画像化せずCodeブロックとしてテキストを保持する（0b(a): 罫線囲みのコード例対策）。
        var figureRegions = new List<NonTextRegion>();
        var codeRegions = new List<(NonTextRegion Region, List<TextLine> Lines)>();
        foreach (var region in regions.Where(r => r.Kind == NonTextRegionKind.Figure))
        {
            var contained = lines.Where(l => OverlapsSignificantly(l.Bounds, region.Bounds)).ToList();
            if (IsCodeRegion(region, contained))
            {
                codeRegions.Add((region, contained));
            }
            else if (IsTextDominatedRegion(region, contained))
            {
                // 罫線表・リンク枠・下線が画像処理で図候補になっても、内部の大部分が
                // 抽出済みテキストなら画像化せず、行をリフロー本文として残す。
                continue;
            }
            else
            {
                figureRegions.Add(region);

                // 雑誌・論文誌では、記事タイトルと著者を大きな扉絵の上へ組むことがある。
                // 図領域内の全文字を消すと、画像は残ってもEPUBの章題・目次が失われる。
                // ページ上部の大きな図に重なる、本文より明確に大きい短文だけをテキストとして
                // 保持する。グラフ中の小さな軸ラベルなどは従来どおり図へ包含する。
                var regionArea = region.Bounds.Width * region.Bounds.Height;
                if (preliminaryBodyHeight > 0
                    && regionArea >= 0.12
                    && region.Bounds.Y < 0.4)
                {
                    foreach (var heading in contained.Where(line =>
                        line.Text.Trim().Length is >= 3 and <= 80
                        && line.Bounds.Height >= preliminaryBodyHeight * 1.5))
                    {
                        preservedFigureHeadings.Add(heading);
                    }
                }
            }
        }

        // 図・コード枠の内部にあるOCR行は、図画像またはCodeブロックに含まれるため、
        // 別の本文段落として重複させない。
        var consumedRegions = figureRegions.Concat(codeRegions.Select(c => c.Region)).ToList();
        var effectiveLines = lines
            .Where(line => preservedFigureHeadings.Contains(line)
                || !consumedRegions.Any(region => OverlapsSignificantly(line.Bounds, region.Bounds)))
            .ToList();

        if (effectiveLines.Count == 0 && figureRegions.Count == 0 && codeRegions.Count == 0)
        {
            return [];
        }

        var bodyHeight = effectiveLines.Count > 0 ? EstimateBodyLineHeight(effectiveLines) : 0;
        var bodyInkDensity = EstimateBodyInkDensity(effectiveLines);

        var items = new List<PositionedItem>(effectiveLines.Count + figureRegions.Count + codeRegions.Count);
        items.AddRange(effectiveLines.Select(l => new PositionedItem(l.Bounds, l, null, null)));
        items.AddRange(figureRegions.Select(r => new PositionedItem(r.Bounds, null, r, null)));
        items.AddRange(codeRegions.Select(c => new PositionedItem(c.Region.Bounds, null, c.Region, c.Lines)));

        var orderedGroups = effectiveLines.Count > 0
            && effectiveLines.All(line => line.SourceReadingOrder is not null)
            ? new List<List<PositionedItem>> { OrderBySourceReadingOrder(items, writingMode) }
            : layoutProfile is null || layoutProfile.UsesGenericColumnDetection
                ? ColumnDetector.DetectColumns(items, item => item.Bounds)
                : ColumnDetector.DetectColumns(
                    items,
                    item => item.Bounds,
                    layoutProfile.ColumnCount,
                    layoutProfile.GutterPosition);

        var blocks = new List<PageBlock>(items.Count);
        var figureBlocks = new List<PageBlock>();
        var readingOrder = 0;

        foreach (var column in orderedGroups)
        {
            var orderedItems = effectiveLines.All(line => line.SourceReadingOrder is not null)
                ? column
                : column.OrderBy(item => item.Bounds.Y).ToList();
            foreach (var item in orderedItems)
            {
                var block = item switch
                {
                    { CodeLines: { } codeLines, Region: { } codeRegion } =>
                        CreateCodeBlock(pageNumber, blocks.Count + 1, codeRegion, codeLines, readingOrder++),
                    { Region: { } figureRegion } =>
                        CreateFigureBlock(pageNumber, blocks.Count + 1, figureRegion, readingOrder++),
                    _ => CreateTextBlock(pageNumber, blocks.Count + 1, item.Line!, bodyHeight, bodyInkDensity, boxedRegions, readingOrder++),
                };

                if (block.Type == BlockType.Figure)
                {
                    figureBlocks.Add(block);
                }

                blocks.Add(block);
            }
        }

        LinkCaptions(blocks, figureBlocks);

        if (writingMode == WritingMode.Vertical)
        {
            foreach (var block in blocks)
            {
                block.Bounds = FromReadingCoordinates(block.Bounds);
            }
        }

        return blocks;
    }

    internal static BoundingBox ToReadingCoordinates(BoundingBox bounds) => new(
        bounds.Y,
        1 - bounds.X - bounds.Width,
        bounds.Height,
        bounds.Width);

    internal static BoundingBox FromReadingCoordinates(BoundingBox bounds) => new(
        1 - bounds.Y - bounds.Height,
        bounds.X,
        bounds.Height,
        bounds.Width);

    private static List<PositionedItem> OrderBySourceReadingOrder(
        IReadOnlyList<PositionedItem> items,
        WritingMode writingMode)
    {
        var textItems = items
            .Where(item => item.Line?.SourceReadingOrder is not null)
            .ToList();
        var lastOrder = textItems.Max(item => item.Line!.SourceReadingOrder!.Value);
        var adjustedTextKeys = new Dictionary<int, double>();
        var figureKeys = new Dictionary<NonTextRegion, double>();

        foreach (var figureItem in items.Where(item => item.Region is { Kind: NonTextRegionKind.Figure }))
        {
            var figure = figureItem.Region!;
            var figureBounds = PageBounds(figure.Bounds, writingMode);
            var captions = textItems
                .Where(textItem => textItem.Line!.SourceType == "キャプション")
                .Where(textItem => IsCaptionForFigure(PageBounds(textItem.Bounds, writingMode), figureBounds))
                .OrderBy(textItem => textItem.Line!.SourceReadingOrder)
                .ToList();
            if (captions.Count == 0)
            {
                continue;
            }

            var firstCaptionOrder = captions[0].Line!.SourceReadingOrder!.Value;
            var lastCaptionOrder = captions[^1].Line!.SourceReadingOrder!.Value;
            var precedingText = textItems
                .Where(textItem => textItem.Line!.SourceType != "キャプション")
                .Where(textItem => textItem.Line!.SourceReadingOrder < firstCaptionOrder)
                .OrderByDescending(textItem => textItem.Line!.SourceReadingOrder)
                .FirstOrDefault();

            // 紙面上の図が文の途中へ割り込む場合、リフローでは文を先に完結させる。
            // 次の句点行まで本文を続け、その直後へ図とキャプションをまとめて置く。
            var placement = firstCaptionOrder - 0.25;
            if (precedingText.Line is not null && !EndsWithTerminalPunctuation(precedingText.Line.Text))
            {
                var sentenceEnd = textItems
                    .Where(textItem => textItem.Line!.SourceType != "キャプション")
                    .Where(textItem => textItem.Line!.SourceReadingOrder > lastCaptionOrder)
                    .OrderBy(textItem => textItem.Line!.SourceReadingOrder)
                    .FirstOrDefault(textItem => EndsWithTerminalPunctuation(textItem.Line!.Text));
                if (sentenceEnd.Line is not null)
                {
                    placement = sentenceEnd.Line!.SourceReadingOrder!.Value + 0.1;
                    for (var index = 0; index < captions.Count; index++)
                    {
                        adjustedTextKeys[captions[index].Line!.SourceReadingOrder!.Value] = placement + 0.1 * (index + 1);
                    }
                }
            }

            figureKeys[figure] = placement;
        }

        double SortKey(PositionedItem item)
        {
            if (item.Line?.SourceReadingOrder is { } sourceOrder)
            {
                return adjustedTextKeys.GetValueOrDefault(sourceOrder, sourceOrder);
            }

            if (item.Region is { Kind: NonTextRegionKind.Figure } figure)
            {
                if (figureKeys.TryGetValue(figure, out var figureKey))
                {
                    return figureKey;
                }

                var figureBounds = PageBounds(figure.Bounds, writingMode);
                var captionOrder = textItems
                    .Where(textItem => textItem.Line!.SourceType == "キャプション")
                    .Where(textItem => IsCaptionForFigure(
                        PageBounds(textItem.Bounds, writingMode),
                        figureBounds))
                    .Select(textItem => textItem.Line!.SourceReadingOrder!.Value)
                    .DefaultIfEmpty(int.MaxValue)
                    .Min();
                if (captionOrder != int.MaxValue)
                {
                    return captionOrder - 0.25;
                }
            }

            return lastOrder + 1 + item.Bounds.Y;
        }

        return items
            .OrderBy(SortKey)
            .ThenBy(item => item.Bounds.Y)
            .ThenBy(item => item.Bounds.X)
            .ToList();
    }

    private static bool EndsWithTerminalPunctuation(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] is '。' or '.' or '！' or '!' or '？' or '?';
    }

    private static BoundingBox PageBounds(BoundingBox bounds, WritingMode writingMode) =>
        writingMode == WritingMode.Vertical ? FromReadingCoordinates(bounds) : bounds;

    private static bool IsCaptionForFigure(BoundingBox caption, BoundingBox figure)
    {
        var gap = caption.Y - (figure.Y + figure.Height);
        var overlapWidth = Math.Max(
            0,
            Math.Min(caption.X + caption.Width, figure.X + figure.Width) - Math.Max(caption.X, figure.X));
        var narrowerWidth = Math.Min(caption.Width, figure.Width);
        return gap is >= -0.02 and <= 0.08
            && narrowerWidth > 0
            && overlapWidth / narrowerWidth >= 0.3;
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

    /// <summary>コード枠内の行を改行区切りで1つのCodeブロックにまとめる（&lt;pre&gt;で行構造を保つ）。</summary>
    private static PageBlock CreateCodeBlock(
        int pageNumber, int blockIndex, NonTextRegion region, IReadOnlyList<TextLine> codeLines, int readingOrder) => new()
    {
        Id = $"p{pageNumber:0000}-b{blockIndex:0000}",
        PageNumber = pageNumber,
        Bounds = region.Bounds,
        Type = BlockType.Code,
        OcrText = string.Join("\n", codeLines.OrderBy(l => l.Bounds.Y).ThenBy(l => l.Bounds.X).Select(l => l.Text)),
        OcrConfidence = codeLines.Count > 0 ? codeLines.Min(l => l.Confidence) : 0,
        TextSource = codeLines.Count > 0 ? codeLines[0].Source : TextSourceKind.Unknown,
        ReadingOrder = readingOrder,
    };

    /// <summary>
    /// 図として検出された領域が「罫線囲みのコード例」かを判定する。条件は、
    /// (1) 複数のテキスト行を含む、(2) 行が領域面積の一定割合以上を覆う（写真・図解は
    /// ラベルが数個で被覆率が低い）、(3) 内容にASCIIのコード記号が一定割合含まれる。
    /// </summary>
    private static bool IsCodeRegion(NonTextRegion region, IReadOnlyList<TextLine> containedLines)
    {
        const double minTextCoverage = 0.25;
        const double minCodeSymbolRatio = 0.05;

        if (containedLines.Count < 2)
        {
            return false;
        }

        var regionArea = region.Bounds.Width * region.Bounds.Height;
        if (regionArea <= 0)
        {
            return false;
        }

        var textArea = containedLines.Sum(l => l.Bounds.Width * l.Bounds.Height);
        if (textArea / regionArea < minTextCoverage)
        {
            return false;
        }

        var nonSpace = 0;
        var codeSymbols = 0;
        foreach (var ch in containedLines.SelectMany(l => l.Text))
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            nonSpace++;
            if (ch is '(' or ')' or '{' or '}' or '[' or ']' or ';' or '=' or '<' or '>'
                or '$' or '#' or '&' or '|' or '\\' or '/' or '*' or '+' or '\'' or '"' or '`' or '_')
            {
                codeSymbols++;
            }
        }

        return nonSpace > 0 && (double)codeSymbols / nonSpace >= minCodeSymbolRatio;
    }

    private static bool IsTextDominatedRegion(NonTextRegion region, IReadOnlyList<TextLine> containedLines)
    {
        const double minTextCoverage = 0.12;

        if (containedLines.Count == 0)
        {
            return false;
        }

        var regionArea = region.Bounds.Width * region.Bounds.Height;
        if (regionArea <= 0)
        {
            return false;
        }

        var textArea = containedLines.Sum(line => line.Bounds.Width * line.Bounds.Height);
        return textArea / regionArea >= minTextCoverage;
    }

    private static PageBlock CreateTextBlock(
        int pageNumber, int blockIndex, TextLine line, double bodyHeight, double? bodyInkDensity, IReadOnlyList<NonTextRegion> boxedRegions, int readingOrder)
    {
        var isBoxed = boxedRegions.Any(b => OverlapsSignificantly(line.Bounds, b.Bounds));
        var type = ClassifyExternalLineType(line.SourceType)
            ?? (isBoxed ? BlockType.Aside : ClassifyLine(line, bodyHeight, bodyInkDensity));
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
            SourceRegionId = line.SourceRegionId,
            ReadingOrder = readingOrder,
            HeadingLevel = HeadingLevelFor(type),
            IsExcluded = isExcluded,
            RequiresReview = line.Confidence < 0.85,
        };
    }

    /// <summary>
    /// 外部OCRの行種別は候補として限定的に利用する。本文は既存分類器へ渡し、
    /// 著者・所属・章見出しのような意味分類はここで確定しない。
    /// </summary>
    private static BlockType? ClassifyExternalLineType(string? sourceType) => sourceType switch
    {
        "キャプション" => BlockType.Caption,
        "注" or "割注" => BlockType.Footnote,
        "柱" => BlockType.Header,
        "ノンブル" => BlockType.PageNumber,
        "文書タイトル" => BlockType.ChapterTitle,
        "タイトル本文" => BlockType.SectionHeading,
        _ => null,
    };

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
                if (candidate is { Type: BlockType.Caption, TextSource: TextSourceKind.NdlOcr })
                {
                    candidate.RelatedBlockId = figure.Id;
                    linkedCount++;
                    continue;
                }

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

    private static BlockType ClassifyLine(TextLine line, double bodyHeight, double? bodyInkDensity)
    {
        var isNearTopMargin = line.Bounds.Y < MarginBand;
        var isNearBottomMargin = line.Bounds.Y + line.Bounds.Height > 1 - MarginBand;
        var isShort = line.Text.Length <= RunningTextMaxLength;

        if (isNearBottomMargin && isShort && line.Text.All(char.IsDigit))
        {
            return BlockType.PageNumber;
        }

        if (line.Bounds.Y >= FootnoteBottomFraction
            && bodyHeight > 0
            && line.Bounds.Height <= bodyHeight * FootnoteMaxHeightRatio
            && BookTextClassifier.LooksLikeFootnote(line.Text))
        {
            return BlockType.Footnote;
        }

        if (BookTextClassifier.ClassifyHeading(line.Text) is { } textHeading)
        {
            return textHeading;
        }

        var ratio = bodyHeight > 0 ? line.Bounds.Height / bodyHeight : 1.0;

        var byHeight = ratio switch
        {
            >= ChapterTitleRatio => BlockType.ChapterTitle,
            >= SectionHeadingRatio => BlockType.SectionHeading,
            >= SubheadingRatio => BlockType.Subheading,
            _ => BlockType.Body,
        };

        // 大きな文字＝章タイトルとは限らない。挿絵・作例内の巨大な文字（漫画の
        // 台詞・見本文字）を章に昇格させると章分割と目次が崩壊するため、
        // 「ページ上部にあり、意味のある長さの英数字を含む」行だけを章タイトルとし、
        // それ以外の巨大文字は装飾として扱う。
        if (byHeight == BlockType.ChapterTitle)
        {
            var trimmed = line.Text.Trim();
            var length = trimmed.Count(c => !char.IsWhiteSpace(c));
            var qualifies = line.Bounds.Y < ChapterTitleMaxTop
                && length >= ChapterTitleMinLength
                && trimmed.Any(char.IsLetterOrDigit)
                && !trimmed.All(c => char.IsDigit(c) || char.IsWhiteSpace(c)) // 表中の大きな数値
                && char.IsLetterOrDigit(trimmed[0]); // 「: WASHER」「-STAR」のようなコード断片
            if (!qualifies)
            {
                return BlockType.Decorative;
            }
        }

        // 高さが本文並みでも、本文より明確にインクが濃い短い行はゴシック太字の見出しと
        // みなす（高さ基準では検出できない 0b(c) のケース）。密度が高すぎる行は
        // 罫線・図形が行ボックスへ重なった可能性が高いため除外する。
        var trimmedLength = line.Text.Count(c => !char.IsWhiteSpace(c));
        if (byHeight == BlockType.Body
            && isShort
            && trimmedLength >= BoldSubheadingMinLength
            && !line.Text.TrimEnd().EndsWith(".", StringComparison.Ordinal)
            && line.InkDensity is { } density
            && bodyInkDensity is { } bodyDensity
            && bodyDensity > 0
            && density >= bodyDensity * BoldSubheadingDensityRatio
            && density < bodyDensity * BoldSubheadingDensityCeiling)
        {
            return BlockType.Subheading;
        }

        // 上下余白にある短い本文は、ここでは柱・フッター候補として段落統合から分離する。
        // 文書全体で反復するかはRepeatedMarginClassifierが後から確認し、反復しない候補は
        // 本文へ戻す。見出し・脚注として判定済みの行は候補へ降格させない。
        if (byHeight == BlockType.Body && isNearTopMargin && isShort)
        {
            return BlockType.Header;
        }

        if (byHeight == BlockType.Body && isNearBottomMargin && isShort)
        {
            return BlockType.Footer;
        }

        return byHeight;
    }

    /// <summary>
    /// 本文のインク密度を中央値で推定する。見出し・図中ラベルの影響を受けにくくするため、
    /// 密度が測定済みの行だけを対象にする。測定行が少なければnull（太字判定を行わない）。
    /// </summary>
    private static double? EstimateBodyInkDensity(IReadOnlyList<TextLine> lines)
    {
        var densities = lines
            .Where(l => l.InkDensity is not null)
            .Select(l => l.InkDensity!.Value)
            .OrderBy(d => d)
            .ToList();

        return densities.Count >= 5 ? densities[densities.Count / 2] : null;
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

    private readonly record struct PositionedItem(
        BoundingBox Bounds, TextLine? Line, NonTextRegion? Region, IReadOnlyList<TextLine>? CodeLines);
}
