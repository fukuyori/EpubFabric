using RapidOcrNet;
using SkiaSharp;
using EpubFabric.Core.Models;
using CoreTextLine = EpubFabric.Core.Models.TextLine;

namespace EpubFabric.Ocr;

public sealed record PageOcrResult(string Text, double AverageConfidence, IReadOnlyList<CoreTextLine> Lines);

/// <summary>
/// 9.5 OCR：ページ画像に対してPP-OCRv6（多言語・日本語含む）でテキスト認識を行う。
/// </summary>
public sealed class PageOcrService : IDisposable
{
    private readonly RapidOcr _ocr = new();
    private bool _initialized;

    public async Task InitializeAsync(OcrModelProvisioner provisioner, CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        var paths = await provisioner.EnsureModelsAsync(cancellationToken);

        var modelSet = RapidOcrModelSet.PPOCRv6Small with
        {
            DetModelPath = paths.DetectorPath,
            RecModelPath = paths.RecognizerPath,
            KeysPath = paths.DictionaryPath,
            ClsModelPath = paths.ClassifierPath,
        };

        _ocr.InitModels(modelSet);
        _initialized = true;
    }

    public PageOcrResult RecognizePage(string imagePath)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException($"{nameof(InitializeAsync)}を先に呼び出してください。");
        }

        var result = _ocr.Detect(imagePath, RapidOcrOptions.PPOCRv6);

        var blockAverages = result.TextBlocks
            .Where(b => b.CharScores is { Length: > 0 })
            .Select(b => b.CharScores!.Average())
            .ToList();

        var confidence = blockAverages.Count > 0 ? blockAverages.Average() : 0.0;

        var imageSize = SKBitmap.DecodeBounds(imagePath);
        var lines = result.TextBlocks
            .Where(b => !string.IsNullOrWhiteSpace(b.Text))
            .Select(b => ToTextLine(b, imageSize.Width, imageSize.Height))
            .ToList();

        return new PageOcrResult(result.StrRes, confidence, lines);
    }

    private static CoreTextLine ToTextLine(TextBlock block, int imageWidth, int imageHeight)
    {
        var minX = block.BoxPoints.Min(p => p.X);
        var maxX = block.BoxPoints.Max(p => p.X);
        var minY = block.BoxPoints.Min(p => p.Y);
        var maxY = block.BoxPoints.Max(p => p.Y);

        var bounds = new EpubFabric.Core.Models.BoundingBox(
            (double)minX / imageWidth,
            (double)minY / imageHeight,
            (double)(maxX - minX) / imageWidth,
            (double)(maxY - minY) / imageHeight);

        var confidence = block.CharScores is { Length: > 0 } ? block.CharScores.Average() : 0.0;

        return new CoreTextLine(bounds, block.Text, confidence, TextSourceKind.Ocr);
    }

    public void Dispose() => _ocr.Dispose();
}
