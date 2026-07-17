namespace EpubFabric.Pdf;

public sealed record PdfPageInfo(
    int PageNumber,
    int WidthPoints,
    int HeightPoints,
    bool HasText);
