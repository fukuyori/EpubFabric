namespace EpubFabric.Pdf;

/// <summary>
/// 9.1 PDF読み込みで確認する基本情報。
/// </summary>
public sealed record PdfDocumentInfo(
    string PdfVersion,
    int PageCount,
    bool HasTextLayer,
    IReadOnlyList<PdfPageInfo> Pages);
