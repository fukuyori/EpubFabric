using System.Xml;

namespace EpubFabric.Pipeline;

/// <summary>公式NDLOCR-Lite CLIをローカルPythonプロセスとして実行する。</summary>
public sealed class NdlOcrService(NdlOcrPipelineOptions options)
{
    public async Task<NdlOcrPageResult> RecognizePageAsync(
        string imagePath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        await ExternalProcessRunner.RunPythonAsync(
            options.PythonExecutable,
            options.ScriptPath,
            [
                "--sourceimg", Path.GetFullPath(imagePath),
                "--output", Path.GetFullPath(outputDirectory),
                "--device", "cpu",
            ],
            options.Timeout,
            cancellationToken);

        var xmlPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(imagePath) + ".xml");
        if (!File.Exists(xmlPath))
        {
            throw new ExternalBackendException($"NDLOCR-LiteのXML出力が見つかりません: {xmlPath}");
        }

        try
        {
            return NdlOcrXmlAdapter.Parse(xmlPath);
        }
        catch (InvalidDataException ex)
        {
            throw new ExternalBackendException($"NDLOCR-LiteのXML出力を使用できません: {ex.Message}", ex);
        }
        catch (XmlException ex)
        {
            throw new ExternalBackendException($"NDLOCR-LiteのXML出力が不正です: {ex.Message}", ex);
        }
    }
}
