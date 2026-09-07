using System.Text.Json;
using SkiaSharp;

namespace EpubFabric.Pipeline;

/// <summary>PP-DocLayoutV2実験ランナーをローカルPythonプロセスとして実行する。</summary>
public sealed class PpDocLayoutService(PpDocLayoutPipelineOptions options)
{
    public async Task<IReadOnlyList<PpDocLayoutRegion>> AnalyzePageAsync(
        string imagePath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        await ExternalProcessRunner.RunPythonAsync(
            options.PythonExecutable,
            options.ScriptPath,
            [
                Path.GetFullPath(imagePath),
                "--output", Path.GetFullPath(outputDirectory),
                "--image-threshold", "0.3",
            ],
            options.Timeout,
            cancellationToken);

        var jsonPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(imagePath) + ".json");
        if (!File.Exists(jsonPath))
        {
            throw new ExternalBackendException($"PP-DocLayoutV2のJSON出力が見つかりません: {jsonPath}");
        }

        var imageSize = SKBitmap.DecodeBounds(imagePath);
        if (imageSize.Width <= 0 || imageSize.Height <= 0)
        {
            throw new ExternalBackendException($"PP-DocLayoutV2入力画像の寸法を読めません: {imagePath}");
        }

        try
        {
            return PpDocLayoutJsonAdapter.Parse(jsonPath, imageSize.Width, imageSize.Height);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            throw new ExternalBackendException($"PP-DocLayoutV2のJSON出力を使用できません: {ex.Message}", ex);
        }
    }
}
