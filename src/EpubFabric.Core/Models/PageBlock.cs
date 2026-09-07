namespace EpubFabric.Core.Models;

public sealed class PageBlock
{
    public required string Id { get; init; }

    public required int PageNumber { get; init; }

    public required BoundingBox Bounds { get; set; }

    public BlockType Type { get; set; }

    public string OcrText { get; set; } = string.Empty;

    public string? CorrectedText { get; set; }

    public double OcrConfidence { get; set; }

    public TextSourceKind TextSource { get; set; }

    public double LayoutConfidence { get; set; }

    public double ClassificationConfidence { get; set; }

    public int ReadingOrder { get; set; }

    public int? HeadingLevel { get; set; }

    public string? RelatedBlockId { get; set; }

    /// <summary>外部解析で同じ領域に属した断片を限定的に再結合するための識別子。</summary>
    public string? SourceRegionId { get; set; }

    public string? ExtractedImagePath { get; set; }

    public bool IsExcluded { get; set; }

    public bool IsManuallyEdited { get; set; }

    public bool RequiresReview { get; set; }
}
