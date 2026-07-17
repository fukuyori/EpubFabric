using EpubFabric.Core.Models;

namespace EpubFabric.Evaluation;

/// <summary>
/// 1ページ分のレイアウト再現度メトリクス。
/// </summary>
public sealed record PageEvaluation(
    int PageNumber,
    int BlockCount,
    IReadOnlyDictionary<string, int> BlockCountsByType,
    int TextCharsTotal,
    int TextCharsIncluded,
    int TextCharsExcluded,
    int TextCharsDropped,
    int FigureCount,
    int FigureWithImageCount,
    int HeadingCount,
    int LowConfidenceIncludedCount,
    double TextCoverage);

/// <summary>
/// 文書全体の集計メトリクス。
/// </summary>
public sealed record EvaluationSummary(
    int PageCount,
    int PagesWithBlocks,
    int TotalBlocks,
    int TextCharsTotal,
    int TextCharsIncluded,
    int TextCharsExcluded,
    int TextCharsDropped,
    int FigureCount,
    int FigureWithImageCount,
    int HeadingCount,
    int LowConfidenceIncludedCount,
    double TextCoverage,
    double FigureImageRate,
    IReadOnlyList<PageEvaluation> Pages);

/// <summary>
/// 解析済みページ群から「EPUBにどれだけの内容が構造付きで載るか」を数値化する。
/// どのブロックがEPUBへ載るかの判定は DocumentBuilder / EpubXhtmlGenerator の
/// 規則（IsExcluded除外、見出しは章タイトル化、空テキスト破棄、図は画像として埋め込み）を写している。
/// </summary>
public sealed class LayoutEvaluator
{
    /// <summary>変換パイプラインの要確認しきい値（OCR信頼度0.85未満）に合わせる。</summary>
    private const double LowConfidenceThreshold = 0.85;

    public EvaluationSummary Evaluate(IReadOnlyList<DocumentPage> pages)
    {
        var pageEvaluations = pages
            .OrderBy(p => p.PageNumber)
            .Select(EvaluatePage)
            .ToList();

        var textCharsTotal = pageEvaluations.Sum(p => p.TextCharsTotal);
        var textCharsIncluded = pageEvaluations.Sum(p => p.TextCharsIncluded);
        var textCharsExcluded = pageEvaluations.Sum(p => p.TextCharsExcluded);
        var figureCount = pageEvaluations.Sum(p => p.FigureCount);
        var figureWithImageCount = pageEvaluations.Sum(p => p.FigureWithImageCount);

        return new EvaluationSummary(
            PageCount: pages.Count,
            PagesWithBlocks: pageEvaluations.Count(p => p.BlockCount > 0),
            TotalBlocks: pageEvaluations.Sum(p => p.BlockCount),
            TextCharsTotal: textCharsTotal,
            TextCharsIncluded: textCharsIncluded,
            TextCharsExcluded: textCharsExcluded,
            TextCharsDropped: pageEvaluations.Sum(p => p.TextCharsDropped),
            FigureCount: figureCount,
            FigureWithImageCount: figureWithImageCount,
            HeadingCount: pageEvaluations.Sum(p => p.HeadingCount),
            LowConfidenceIncludedCount: pageEvaluations.Sum(p => p.LowConfidenceIncludedCount),
            TextCoverage: Coverage(textCharsIncluded, textCharsTotal - textCharsExcluded),
            FigureImageRate: figureCount == 0 ? 1.0 : (double)figureWithImageCount / figureCount,
            Pages: pageEvaluations);
    }

    private static PageEvaluation EvaluatePage(DocumentPage page)
    {
        var countsByType = page.Blocks
            .GroupBy(b => b.Type)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        var textCharsTotal = 0;
        var textCharsIncluded = 0;
        var textCharsExcluded = 0;
        var lowConfidenceIncluded = 0;

        foreach (var block in page.Blocks)
        {
            var chars = CountTextChars(block);
            textCharsTotal += chars;

            if (block.IsExcluded)
            {
                textCharsExcluded += chars;
                continue;
            }

            // 図ブロック内の文字は画像として表現されるため、テキスト欠落として数えない。
            if (block.Type == BlockType.Figure)
            {
                textCharsExcluded += chars;
                continue;
            }

            if (chars > 0)
            {
                textCharsIncluded += chars;
                if (block.OcrConfidence < LowConfidenceThreshold)
                {
                    lowConfidenceIncluded++;
                }
            }
        }

        var figures = page.Blocks.Where(b => b.Type == BlockType.Figure && !b.IsExcluded).ToList();

        return new PageEvaluation(
            PageNumber: page.PageNumber,
            BlockCount: page.Blocks.Count,
            BlockCountsByType: countsByType,
            TextCharsTotal: textCharsTotal,
            TextCharsIncluded: textCharsIncluded,
            TextCharsExcluded: textCharsExcluded,
            TextCharsDropped: textCharsTotal - textCharsIncluded - textCharsExcluded,
            FigureCount: figures.Count,
            FigureWithImageCount: figures.Count(b => b.ExtractedImagePath is not null),
            HeadingCount: page.Blocks.Count(b => !b.IsExcluded && b.Type is BlockType.ChapterTitle or BlockType.SectionHeading or BlockType.Subheading),
            LowConfidenceIncludedCount: lowConfidenceIncluded,
            TextCoverage: Coverage(textCharsIncluded, textCharsTotal - textCharsExcluded));
    }

    private static double Coverage(int included, int denominator) =>
        denominator <= 0 ? 1.0 : (double)included / denominator;

    private static int CountTextChars(PageBlock block)
    {
        var text = block.CorrectedText ?? block.OcrText;
        return string.IsNullOrEmpty(text) ? 0 : text.Count(c => !char.IsWhiteSpace(c));
    }
}
