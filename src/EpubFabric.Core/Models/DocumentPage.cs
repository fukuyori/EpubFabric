namespace EpubFabric.Core.Models;

public sealed class DocumentPage
{
    public required int PageNumber { get; init; }

    public required string OriginalImagePath { get; set; }

    public required string ProcessedImagePath { get; set; }

    public required string PreviewImagePath { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public WritingMode WritingMode { get; set; }

    public List<PageBlock> Blocks { get; init; } = [];

    public PageProcessingStatus Status { get; set; }
}
