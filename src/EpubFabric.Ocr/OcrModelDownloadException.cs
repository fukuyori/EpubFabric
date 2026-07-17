namespace EpubFabric.Ocr;

/// <summary>
/// 16章「OCRモデルがない」に対応する例外。ダウンロード失敗やハッシュ不一致で送出される。
/// </summary>
public sealed class OcrModelDownloadException : Exception
{
    public OcrModelDownloadException(string message)
        : base(message)
    {
    }

    public OcrModelDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
