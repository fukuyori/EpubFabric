namespace EpubFabric.Pdf;

/// <summary>
/// PDFを開けない場合の例外（16章「PDFを開けない」「暗号化PDF」に対応）。
/// </summary>
public sealed class PdfLoadException : Exception
{
    public PdfLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
