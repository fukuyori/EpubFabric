using EpubFabric.Core.Models;
using EpubFabric.Persistence;

namespace EpubFabric.Tests;

public class EfprojStoreTests
{
    [Fact]
    public void SaveThenLoad_RoundTripsProjectAndPreservesManualCorrections()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"epubfabric-test-page-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(imagePath, [1, 2, 3, 4]);

        var block = new PageBlock
        {
            Id = "p0001-b0001",
            PageNumber = 1,
            Bounds = new BoundingBox(0, 0, 1, 1),
            Type = BlockType.Body,
            OcrText = "誤認識されたテキスト",
            ReadingOrder = 0,
        };

        var page = new DocumentPage
        {
            PageNumber = 1,
            OriginalImagePath = imagePath,
            ProcessedImagePath = imagePath,
            PreviewImagePath = imagePath,
            WritingMode = WritingMode.Horizontal,
        };
        page.Blocks.Add(block);

        var project = new EpubFabricProject
        {
            Id = Guid.NewGuid(),
            Title = "テスト書籍",
            SourcePdfPath = "dummy.pdf",
            Pages = [page],
        };

        var projectDirectory = Path.Combine(Path.GetTempPath(), $"epubfabric-test-{Guid.NewGuid():N}.efproj");
        var store = new EfprojStore();

        try
        {
            store.Save(project, projectDirectory);

            // 校正前: blocks/text の内容はOCR結果のままなので、CorrectedTextは設定されない。
            var reloaded = store.Load(projectDirectory);
            var reloadedBlock = Assert.Single(reloaded.Pages.Single().Blocks);
            Assert.Equal("誤認識されたテキスト", reloadedBlock.OcrText);
            Assert.Null(reloadedBlock.CorrectedText);
            Assert.False(reloadedBlock.IsManuallyEdited);

            // 校正: テキストファイルを直接編集する（GUI校正画面の代替）。
            var textPath = Path.Combine(projectDirectory, "blocks", "text", $"{block.Id}.txt");
            Assert.True(File.Exists(textPath));
            File.WriteAllText(textPath, "正しいテキスト");

            var correctedProject = store.Load(projectDirectory);
            var correctedBlock = Assert.Single(correctedProject.Pages.Single().Blocks);
            Assert.Equal("誤認識されたテキスト", correctedBlock.OcrText);
            Assert.Equal("正しいテキスト", correctedBlock.CorrectedText);
            Assert.True(correctedBlock.IsManuallyEdited);

            // ページ画像はプロジェクトフォルダー内にコピーされる。
            Assert.StartsWith(projectDirectory, correctedProject.Pages.Single().OriginalImagePath);
        }
        finally
        {
            File.Delete(imagePath);
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    [Fact]
    public void Save_DoesNotOverwriteExistingManualCorrection()
    {
        var block = new PageBlock
        {
            Id = "p0001-b0001",
            PageNumber = 1,
            Bounds = new BoundingBox(0, 0, 1, 1),
            Type = BlockType.Body,
            OcrText = "OCRテキスト",
        };

        var imagePath = Path.Combine(Path.GetTempPath(), $"epubfabric-test-page-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(imagePath, [1, 2, 3, 4]);

        var page = new DocumentPage
        {
            PageNumber = 1,
            OriginalImagePath = imagePath,
            ProcessedImagePath = imagePath,
            PreviewImagePath = imagePath,
        };
        page.Blocks.Add(block);

        var project = new EpubFabricProject
        {
            Id = Guid.NewGuid(),
            Title = "テスト書籍",
            SourcePdfPath = "dummy.pdf",
            Pages = [page],
        };

        var projectDirectory = Path.Combine(Path.GetTempPath(), $"epubfabric-test-{Guid.NewGuid():N}.efproj");
        var store = new EfprojStore();

        try
        {
            store.Save(project, projectDirectory);

            var textPath = Path.Combine(projectDirectory, "blocks", "text", $"{block.Id}.txt");
            File.WriteAllText(textPath, "利用者による校正済みテキスト");

            // 再解析（analyzeの再実行）を模擬しても、既存の校正済みファイルは上書きされない。
            store.Save(project, projectDirectory);

            Assert.Equal("利用者による校正済みテキスト", File.ReadAllText(textPath));
        }
        finally
        {
            File.Delete(imagePath);
            Directory.Delete(projectDirectory, recursive: true);
        }
    }
}
