namespace EpubFabric.Core.Models;

public enum PageProcessingStatus
{
    NotProcessed,
    ImageReady,
    LayoutAnalyzed,
    OcrCompleted,
    Classified,
    ReviewRequired,
    Reviewed,
    Error
}
