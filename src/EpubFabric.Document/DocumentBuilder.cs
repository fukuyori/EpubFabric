using EpubFabric.Core.Models;

namespace EpubFabric.Document;

/// <summary>
/// 9.8 文書構造化：ページ単位のブロックを書籍全体の章構造へ変換する。
/// 見出し（ChapterTitle・SectionHeading）が1つも検出されていない場合（テキストレイヤー
/// からの抽出のみで、まだレイアウト解析を通していない場合など）は、従来どおり全体を
/// 1つの章にまとめる。見出しが検出されている場合は、見出しごとに章を区切る（見出しの
/// 階層をそのまま章・節として使うのは簡易な近似であり、厳密な章構造の判定は
/// Ollama連携（第3段階）で補正する）。
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

        var hasHeadings = orderedBlocks.Any(b => b.Type is BlockType.ChapterTitle or BlockType.SectionHeading);
        if (!hasHeadings)
        {
            return [BuildSingleChapter(fallbackTitle, orderedBlocks)];
        }

        var sections = new List<(string Title, int HeadingLevel, List<string> BlockIds)>();

        foreach (var block in orderedBlocks)
        {
            if (block.Type is BlockType.ChapterTitle or BlockType.SectionHeading)
            {
                var title = string.IsNullOrWhiteSpace(block.CorrectedText) ? block.OcrText : block.CorrectedText;
                sections.Add((title, block.HeadingLevel ?? 1, []));
                continue; // 見出し自体はTitleへ引き継ぐため、本文ブロックには含めない。
            }

            if (sections.Count == 0)
            {
                // 最初の見出しより前に現れるブロック（表紙情報など）用の章。
                sections.Add((fallbackTitle, 1, []));
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

    private static DocumentChapter BuildSingleChapter(string title, List<PageBlock> blocks)
    {
        var chapter = new DocumentChapter { Id = "chapter-001", Title = title, HeadingLevel = 1 };
        chapter.BlockIds.AddRange(blocks.Select(b => b.Id));
        return chapter;
    }
}
