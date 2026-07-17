namespace EpubFabric.Core.Models;

/// <summary>
/// EPUBの透明テキスト層へ使用する文字情報の取得元。
/// </summary>
public enum TextSourceKind
{
    Unknown,
    PdfTextLayer,
    Ocr,
}
