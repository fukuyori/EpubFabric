using SkiaSharp;

namespace EpubFabric.Epub;

/// <summary>EPUBへ収録するページ画像。<see cref="Bytes"/>がnullなら<see cref="SourcePath"/>をそのまま収録する。</summary>
public sealed record PreparedPageImage(string FileName, byte[]? Bytes, string SourcePath);

/// <summary>
/// ページ画像をEPUBへ収録できる形に整える。300dpiのページPNGをそのまま収録すると
/// 1冊で数百MBになるため、長辺が上限を超える画像は縮小してJPEG化し、上限内でも
/// 大きなPNGはJPEG化する。小さな画像や既存のJPEGは無加工で収録する。
/// 固定レイアウト（全ページ）とリフロー型（表紙ページのみ）の両方で使う。
/// </summary>
public sealed class PageImageTranscoder
{
    /// <summary>ページ画像をJPEG化する際の品質。スキャン紙面の文字が読める下限より余裕を持たせた値。</summary>
    public const int DefaultJpegQuality = 85;

    /// <summary>ページ画像の長辺の上限。一般的な電子書籍端末・タブレットの表示には2200pxで十分。</summary>
    public const int DefaultMaxImageSideLength = 2200;

    /// <summary>このサイズ以下のPNGは再圧縮しない（小さな画像はJPEG化の劣化に見合わない）。</summary>
    private const long PngRecompressionThresholdBytes = 200 * 1024;

    private readonly int _jpegQuality;
    private readonly int _maxImageSideLength;

    /// <param name="jpegQuality">JPEG化する際の品質（1～100）。</param>
    /// <param name="maxImageSideLength">長辺の上限px。0以下で無制限。</param>
    public PageImageTranscoder(int jpegQuality = DefaultJpegQuality, int maxImageSideLength = DefaultMaxImageSideLength)
    {
        _jpegQuality = Math.Clamp(jpegQuality, 1, 100);
        _maxImageSideLength = maxImageSideLength <= 0 ? int.MaxValue : maxImageSideLength;
    }

    /// <param name="sourcePath">収録元の画像パス。</param>
    /// <param name="baseName">拡張子を除いた収録先ファイル名。</param>
    public PreparedPageImage Prepare(string sourcePath, string baseName)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".webp")
        {
            throw new NotSupportedException($"EPUBへ収録できないページ画像形式です: {extension}");
        }

        var bounds = SKBitmap.DecodeBounds(sourcePath);
        var longSide = Math.Max(bounds.Width, bounds.Height);
        var needsTranscode = bounds.Width > 0
            && (longSide > _maxImageSideLength
                || (extension == ".png" && new FileInfo(sourcePath).Length > PngRecompressionThresholdBytes));

        return needsTranscode
            ? new PreparedPageImage($"{baseName}.jpg", TranscodeToJpeg(sourcePath), sourcePath)
            : new PreparedPageImage($"{baseName}{extension}", null, sourcePath);
    }

    private byte[] TranscodeToJpeg(string sourcePath)
    {
        using var original = SKBitmap.Decode(sourcePath)
            ?? throw new NotSupportedException($"ページ画像を読み込めません: {sourcePath}");

        var scale = Math.Min(1.0, (double)_maxImageSideLength / Math.Max(original.Width, original.Height));
        var width = Math.Max(1, (int)Math.Round(original.Width * scale));
        var height = Math.Max(1, (int)Math.Round(original.Height * scale));

        // JPEGは透過を持てないため、白地に合成しながら目的サイズへ描画する。
        using var opaque = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(opaque))
        using (var sourceImage = SKImage.FromBitmap(original))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawImage(
                sourceImage,
                new SKRect(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        using var image = SKImage.FromBitmap(opaque);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, _jpegQuality);
        return data.ToArray();
    }

    /// <summary>表示に使うページ画像のパス。--enhance適用時の高品質化画像があればそちらを優先する。</summary>
    public static string DisplayImagePathOf(EpubFabric.Core.Models.DocumentPage page) =>
        !string.IsNullOrEmpty(page.ProcessedImagePath) && File.Exists(page.ProcessedImagePath)
            ? page.ProcessedImagePath
            : page.OriginalImagePath;

    public static string MediaTypeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => "image/png",
    };
}
