using System.Security.Cryptography;

namespace EpubFabric.Ocr;

internal sealed record OcrModelFile(string FileName, string DownloadUrl, string? Sha256);

public sealed record OcrModelPaths(string DetectorPath, string RecognizerPath, string DictionaryPath, string ClassifierPath);

/// <summary>
/// PP-OCRv6の多言語（日本語含む）モデルを初回利用時にローカルへダウンロードする。
/// RapidOcrNetのNuGetパッケージにはラテン文字専用モデルしか同梱されていないため、
/// 日本語などCJKを含む文書には別途この多言語モデルが必要になる（16章「OCRモデルがない」）。
/// ダウンロード元はRapidOCRプロジェクトの公式モデル一覧（RapidAI/RapidOCR）。
/// </summary>
public sealed class OcrModelProvisioner
{
    // https://github.com/RapidAI/RapidOCR/blob/main/python/rapidocr/default_models.yaml (onnx / PP-OCRv6) より。
    private static readonly OcrModelFile Detector = new(
        "PP-OCRv6_det_small.onnx",
        "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.1/onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx",
        "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f");

    private static readonly OcrModelFile Recognizer = new(
        "PP-OCRv6_rec_small.onnx",
        "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.1/onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx",
        "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884");

    // 辞書ファイルはyaml上でハッシュが公開されていないため検証を省略する。
    private static readonly OcrModelFile Dictionary = new(
        "ppocrv6_small_dict.txt",
        "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.1/paddle/PP-OCRv6/rec/PP-OCRv6_rec_small/ppocrv6_dict.txt",
        null);

    private readonly string _cacheDirectory;
    private readonly HttpClient _httpClient;

    public OcrModelProvisioner(HttpClient? httpClient = null, string? cacheDirectory = null)
    {
        _httpClient = httpClient ?? CreateDefaultHttpClient();
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EpubFabric", "models", "ppocrv6-small");
    }

    public async Task<OcrModelPaths> EnsureModelsAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_cacheDirectory);

        var detPath = await EnsureFileAsync(Detector, cancellationToken);
        var recPath = await EnsureFileAsync(Recognizer, cancellationToken);
        var dictPath = await EnsureFileAsync(Dictionary, cancellationToken);
        var clsPath = EnsureClassifierModel();

        return new OcrModelPaths(detPath, recPath, dictPath, clsPath);
    }

    /// <summary>
    /// 向き分類モデルはRapidOcrNetパッケージに同梱されているが、ビルド出力へコピーする
    /// MSBuildターゲットがProjectReference越しには伝播しないため、EpubFabric.Ocr自身に
    /// 埋め込みリソースとして同梱し、初回利用時にキャッシュへ展開する。
    /// </summary>
    public string EnsureClassifierModel()
    {
        const string resourceName = "EpubFabric.Ocr.Models.ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx";
        const string expectedSha256 = "54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7";

        Directory.CreateDirectory(_cacheDirectory);

        var path = Path.Combine(_cacheDirectory, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");

        if (File.Exists(path) && MatchesHash(path, expectedSha256))
        {
            return path;
        }

        using var resourceStream = typeof(OcrModelProvisioner).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new OcrModelDownloadException($"埋め込みリソースが見つかりません: {resourceName}");

        var tempPath = path + ".extract";
        using (var fileStream = File.Create(tempPath))
        {
            resourceStream.CopyTo(fileStream);
        }

        if (!MatchesHash(tempPath, expectedSha256))
        {
            File.Delete(tempPath);
            throw new OcrModelDownloadException($"埋め込み分類モデルのハッシュが一致しません: {resourceName}");
        }

        File.Move(tempPath, path, overwrite: true);
        return path;
    }

    private async Task<string> EnsureFileAsync(OcrModelFile file, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_cacheDirectory, file.FileName);

        if (File.Exists(path) && (file.Sha256 is null || MatchesHash(path, file.Sha256)))
        {
            return path;
        }

        var tempPath = path + ".download";

        try
        {
            using var response = await _httpClient.GetAsync(file.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = File.Create(tempPath))
            {
                await httpStream.CopyToAsync(fileStream, cancellationToken);
            }

            if (file.Sha256 is not null && !MatchesHash(tempPath, file.Sha256))
            {
                throw new OcrModelDownloadException($"ダウンロードしたOCRモデルのハッシュが一致しません: {file.FileName}");
            }

            File.Move(tempPath, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is not OcrModelDownloadException)
        {
            throw new OcrModelDownloadException($"OCRモデルのダウンロードに失敗しました: {file.FileName}（{file.DownloadUrl}）", ex);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient();

        // modelscope.cnはUser-Agent未設定のリクエストを403で拒否するため設定する。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EpubFabric/1.0 (+https://github.com/fukuyori/EpubFabric)");

        return client;
    }

    private static bool MatchesHash(string path, string expectedSha256)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}
