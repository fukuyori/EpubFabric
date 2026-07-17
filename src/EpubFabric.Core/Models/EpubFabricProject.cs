namespace EpubFabric.Core.Models;

public sealed class EpubFabricProject
{
    public required Guid Id { get; init; }

    public required string Title { get; set; }

    public string? Author { get; set; }

    public string? Publisher { get; set; }

    public required string SourcePdfPath { get; init; }

    public string Language { get; set; } = "ja";

    public WritingMode WritingMode { get; set; }

    public List<DocumentPage> Pages { get; init; } = [];

    public List<DocumentChapter> Chapters { get; init; } = [];

    public EpubFabricSettings Settings { get; init; } = new();
}
