namespace EpubFabric.Core.Models;

/// <summary>
/// 文書構造画面（11.7）でツリー表示される章・節の単位。
/// </summary>
public sealed class DocumentChapter
{
    public required string Id { get; init; }

    public required string Title { get; set; }

    public int HeadingLevel { get; set; }

    public List<string> BlockIds { get; init; } = [];

    public List<DocumentChapter> Children { get; init; } = [];
}
