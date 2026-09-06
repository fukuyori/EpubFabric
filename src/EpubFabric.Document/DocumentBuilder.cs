using EpubFabric.Core.Models;

namespace EpubFabric.Document;

/// <summary>
/// 9.8 文書構造化：ページ単位のブロックを書籍全体の章構造へ変換する。
/// 章の区切りに使うのはChapterTitle（章・記事の大見出し）のみ。SectionHeading・
/// Subheadingは章内の見出し（h2/h3）として本文に残す。かつてはSectionHeadingでも
/// 章を区切っていたが、それでは節見出しがすべて章タイトル（常にh1描画）に消費されて
/// h2が出力されず、誤分類された本文断片まで章になってしまう。ChapterTitleが1つも
/// 検出されていない場合は全体を1つの章にまとめる。
/// </summary>
public sealed class DocumentBuilder
{
    public IReadOnlyList<DocumentChapter> BuildChapters(IReadOnlyList<DocumentPage> pages, string fallbackTitle)
    {
        var orderedBlocks = pages
            .OrderBy(p => p.PageNumber)
            .SelectMany(p => p.Blocks.OrderBy(b => b.ReadingOrder))
            .Where(b => !b.IsExcluded)
            .ToList();

        var hasHeadings = orderedBlocks.Any(b => b.Type is BlockType.ChapterTitle);
        if (!hasHeadings)
        {
            return [BuildSingleChapter(fallbackTitle, orderedBlocks)];
        }

        // これより本文ブロックが少ない章は独立させない。誤検出された章タイトルが近接して
        // 連続するケース（表の各行が巨大な文字で章化する等）で、目次が断片だらけになるのを防ぐ。
        // 表紙・前付け（フォールバック章）の直後の最初の章タイトルは常に分割を許可する。
        const int minBlocksBeforeSplit = 3;

        var sections = new List<(string Title, int HeadingLevel, List<string> BlockIds)>();
        var lastSectionIsFallback = false;
        var seenChapterTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var block in orderedBlocks)
        {
            if (block.Type is BlockType.ChapterTitle)
            {
                var candidateTitle = string.IsNullOrWhiteSpace(block.CorrectedText) ? block.OcrText : block.CorrectedText;
                if (string.IsNullOrWhiteSpace(block.CorrectedText) && !IsStructuralChapterTitle(candidateTitle))
                {
                    // 大きな節見出し・コード表のセル・本文断片が高さだけでChapterTitleに
                    // なっても章分割には使わない。h3へ降格し、本文の位置には残す。
                    block.Type = BlockType.Subheading;
                    block.HeadingLevel = 3;
                }
                else
                {
                    var titleKey = NormalizeTitle(candidateTitle);
                    if (!seenChapterTitles.Add(titleKey))
                    {
                        // 印刷目次や部扉に章名が再掲された場合、同名章を重複生成しない。
                        block.IsExcluded = true;
                        continue;
                    }
                }
            }

            if (block.Type is BlockType.ChapterTitle)
            {
                if (sections.Count > 0
                    && !lastSectionIsFallback
                    && sections[^1].BlockIds.Count < minBlocksBeforeSplit)
                {
                    // 直前の章がまだ十分な本文を持たないうちに次の章タイトルが来た場合は
                    // 章を分割せず、本文内のh1として残す。
                    sections[^1].BlockIds.Add(block.Id);
                    continue;
                }

                var title = string.IsNullOrWhiteSpace(block.CorrectedText) ? block.OcrText : block.CorrectedText;
                sections.Add((title, block.HeadingLevel ?? 1, []));
                lastSectionIsFallback = false;
                continue; // 見出し自体はTitleへ引き継ぐため、本文ブロックには含めない。
            }

            if (sections.Count == 0)
            {
                // 最初の見出しより前に現れるブロック（表紙情報など）用の章。
                sections.Add((fallbackTitle, 1, []));
                lastSectionIsFallback = true;
            }

            sections[^1].BlockIds.Add(block.Id);
        }

        return sections
            .Select((section, index) => new DocumentChapter
            {
                Id = $"chapter-{index + 1:000}",
                Title = section.Title,
                HeadingLevel = section.HeadingLevel,
                BlockIds = section.BlockIds,
            })
            .ToList();
    }

    private static bool IsStructuralChapterTitle(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return false;
        }

        if (words[0].Equals("chapter", StringComparison.OrdinalIgnoreCase))
        {
            return words.Length <= 10
                && words.Length >= 3
                && IsNumberToken(words[1]);
        }

        if (words[0].Equals("appendix", StringComparison.OrdinalIgnoreCase)
            || words[0].Equals("part", StringComparison.OrdinalIgnoreCase))
        {
            return words.Length <= 12 && words.Length >= 2;
        }

        if (words[0].Equals("preface", StringComparison.OrdinalIgnoreCase)
            || words[0].Equals("foreword", StringComparison.OrdinalIgnoreCase)
            || words[0].Equals("introduction", StringComparison.OrdinalIgnoreCase)
            || words[0].Equals("prologue", StringComparison.OrdinalIgnoreCase)
            || words[0].Equals("epilogue", StringComparison.OrdinalIgnoreCase))
        {
            return words.Length <= 8;
        }

        if (IsNumberToken(words[0]))
        {
            return words.Length >= 2 && words.Length <= 12;
        }

        var chapterMarker = trimmed.IndexOf('章');
        return chapterMarker is >= 1 and <= 12;
    }

    private static bool IsNumberToken(string token)
    {
        return token.EndsWith(".", StringComparison.Ordinal)
            && token.Length > 1
            && token[..^1].All(char.IsAsciiDigit);
    }

    private static string NormalizeTitle(string title) => string.Join(
        ' ',
        title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static DocumentChapter BuildSingleChapter(string title, List<PageBlock> blocks)
    {
        var chapter = new DocumentChapter { Id = "chapter-001", Title = title, HeadingLevel = 1 };
        chapter.BlockIds.AddRange(blocks.Select(b => b.Id));
        return chapter;
    }
}
