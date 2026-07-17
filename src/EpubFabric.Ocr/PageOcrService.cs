using RapidOcrNet;

namespace EpubFabric.Ocr;

public sealed record PageOcrResult(string Text, double AverageConfidence);

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

        return new PageOcrResult(result.StrRes, confidence);
    }

    public void Dispose() => _ocr.Dispose();
}
