using EpubFabric.Core.Models;

namespace EpubFabric.Document;

/// <summary>
/// 9.8 文書構造化：ページ単位のブロックを書籍全体の章構造へ変換する。
/// 見出し階層による章・節の分割は、レイアウト解析とOllama連携（第2・3段階）を
/// 追加した後に対応する。現段階では除外されていないブロックを読み順に1つの章へまとめる。
/// </summary>
public sealed class DocumentBuilder
{
    public IReadOnlyList<DocumentChapter> BuildChapters(IReadOnlyList<DocumentPage> pages, string chapterTitle)
    {
        var blockIds = pages
            .OrderBy(p => p.PageNumber)
            .SelectMany(p => p.Blocks.OrderBy(b => b.ReadingOrder))
            .Where(b => !b.IsExcluded)
            .Select(b => b.Id)
            .ToList();

        var chapter = new DocumentChapter
        {
            Id = "chapter-001",
            Title = chapterTitle,
            HeadingLevel = 1,
        };
        chapter.BlockIds.AddRange(blockIds);

        return [chapter];
    }
}
