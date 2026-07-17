using System.Text.Json;
using System.Text.Json.Serialization;
using EpubFabric.Core.Models;

namespace EpubFabric.Persistence;

/// <summary>
/// 8章 作業ファイル形式：.efprojをフォルダー形式で保存・読込する（8.3 初期バージョンの方針）。
/// 校正結果は blocks/text/{blockId}.txt に平文で保持する。手元のテキストエディタで直接
/// 補正できるようにするための、GUI校正画面（11.5）が未実装の段階での代替手段である。
/// project.json にはOCR結果（不変の元データ）を保持し、補正はテキストファイル側にのみ書き込む
/// ことで、24章「元の情報と解析結果を分離して保持する」「AIの出力によって元の文字列を直接
/// 変更しない」という原則を守る。
/// </summary>
public sealed class EfprojStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public void Save(EpubFabricProject project, string projectDirectory)
    {
        var pagesDirectory = Path.Combine(projectDirectory, "pages", "original");
        var textDirectory = Path.Combine(projectDirectory, "blocks", "text");
        Directory.CreateDirectory(pagesDirectory);
        Directory.CreateDirectory(textDirectory);

        foreach (var page in project.Pages)
        {
            page.OriginalImagePath = CopyPageImage(page.OriginalImagePath, pagesDirectory, page.PageNumber);
            page.ProcessedImagePath = page.OriginalImagePath;
            page.PreviewImagePath = page.OriginalImagePath;
        }

        File.WriteAllText(
            Path.Combine(projectDirectory, "project.json"),
            JsonSerializer.Serialize(project, JsonOptions));

        foreach (var block in project.Pages.SelectMany(p => p.Blocks))
        {
            var textPath = BlockTextPath(projectDirectory, block.Id);

            // 既存の補正済みファイルは上書きしない（15.4「手動修正済み項目は上書きしない」）。
            if (!File.Exists(textPath))
            {
                File.WriteAllText(textPath, Normalize(block.CorrectedText ?? block.OcrText));
            }
        }
    }

    public EpubFabricProject Load(string projectDirectory)
    {
        var projectJsonPath = Path.Combine(projectDirectory, "project.json");
        if (!File.Exists(projectJsonPath))
        {
            throw new FileNotFoundException($"プロジェクトファイルが見つかりません: {projectJsonPath}", projectJsonPath);
        }

        var project = JsonSerializer.Deserialize<EpubFabricProject>(File.ReadAllText(projectJsonPath), JsonOptions)
            ?? throw new InvalidDataException($"プロジェクトを読み込めませんでした: {projectJsonPath}");

        foreach (var block in project.Pages.SelectMany(p => p.Blocks))
        {
            var textPath = BlockTextPath(projectDirectory, block.Id);
            if (!File.Exists(textPath))
            {
                continue;
            }

            var editedText = Normalize(File.ReadAllText(textPath));
            if (editedText != Normalize(block.OcrText))
            {
                block.CorrectedText = editedText;
                block.IsManuallyEdited = true;
            }
        }

        return project;
    }

    private static string CopyPageImage(string sourcePath, string pagesDirectory, int pageNumber)
    {
        var destinationPath = Path.Combine(pagesDirectory, $"page-original-{pageNumber:0000}.png");

        if (Path.GetFullPath(sourcePath) == Path.GetFullPath(destinationPath))
        {
            return destinationPath;
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
        return destinationPath;
    }

    private static string BlockTextPath(string projectDirectory, string blockId) =>
        Path.Combine(projectDirectory, "blocks", "text", $"{blockId}.txt");

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();
}
