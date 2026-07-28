using System.IO.Compression;
using EpubFabric.Core.Models;
using EpubFabric.Document;
using EpubFabric.Epub;
using SkiaSharp;

namespace EpubFabric.Tests;

public class EpubPackageBuilderTests
{
    [Fact]
    public void Build_WritesValidEpubPackageStructure()
    {
        var block = new PageBlock
        {
            Id = "p0001-b0001",
            PageNumber = 1,
            Bounds = new BoundingBox(0, 0, 1, 1),
            Type = BlockType.Body,
            OcrText = "本文テキスト",
        };

        var chapter = new DocumentChapter { Id = "chapter-001", Title = "第1章" };
        chapter.BlockIds.Add(block.Id);

        var project = new EpubFabricProject
        {
            Id = Guid.NewGuid(),
            Title = "テスト書籍",
            SourcePdfPath = "dummy.pdf",
        };

        var outputPath = Path.Combine(Path.GetTempPath(), $"epubfabric-test-{Guid.NewGuid():N}.epub");

        try
        {
            new EpubPackageBuilder().Build(project, [chapter], new Dictionary<string, PageBlock> { [block.Id] = block }, outputPath);

            using var zip = ZipFile.OpenRead(outputPath);
            var entries = zip.Entries.Select(e => e.FullName).ToList();

            Assert.Equal("mimetype", entries[0]);
            Assert.Contains("META-INF/container.xml", entries);
            Assert.Contains("EPUB/package.opf", entries);
            Assert.Contains("EPUB/nav.xhtml", entries);
            Assert.Contains("EPUB/styles/book.css", entries);
            Assert.Contains("EPUB/text/chapter-001.xhtml", entries);

            var mimetypeEntry = zip.GetEntry("mimetype")!;
            Assert.Equal(mimetypeEntry.Length, mimetypeEntry.CompressedLength);

            using var reader = new StreamReader(mimetypeEntry.Open());
            Assert.Equal("application/epub+zip", reader.ReadToEnd());
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    /// <summary>
    /// リフロー型でも1ページ目をページ画像のまま表紙として収録できる（--cover-image）。
    /// 表紙のOCRは装飾文字の誤読が多く本文に適さないため、画像で見せる選択肢を用意している。
    /// </summary>
    [Fact]
    public void Build_リフロー型でも1ページ目を表紙画像として収録できる()
    {
        var imagePath = CreatePagePng();

        var block = new PageBlock
        {
            Id = "p0002-b0001",
            PageNumber = 2,
            Bounds = new BoundingBox(0, 0, 1, 1),
            Type = BlockType.Body,
            OcrText = "本文テキスト",
        };

        var chapter = new DocumentChapter { Id = "chapter-001", Title = "第1章" };
        chapter.BlockIds.Add(block.Id);

        var project = new EpubFabricProject
        {
            Id = Guid.NewGuid(),
            Title = "表紙付き書籍",
            SourcePdfPath = "dummy.pdf",
        };
        project.Pages.Add(new DocumentPage
        {
            PageNumber = 1,
            OriginalImagePath = imagePath,
            ProcessedImagePath = imagePath,
            PreviewImagePath = imagePath,
        });

        var outputPath = Path.Combine(Path.GetTempPath(), $"epubfabric-test-{Guid.NewGuid():N}.epub");

        try
        {
            new EpubPackageBuilder(new PageImageTranscoder())
                .Build(project, [chapter], new Dictionary<string, PageBlock> { [block.Id] = block }, outputPath);

            using var zip = ZipFile.OpenRead(outputPath);
            var entries = zip.Entries.Select(e => e.FullName).ToList();

            Assert.Contains("EPUB/text/cover.xhtml", entries);
            Assert.Contains(entries, e => e.StartsWith("EPUB/images/cover.", StringComparison.Ordinal));

            using var opfReader = new StreamReader(zip.GetEntry("EPUB/package.opf")!.Open());
            var opf = opfReader.ReadToEnd();

            Assert.Contains("properties=\"cover-image\"", opf);
            // 表紙は本文より先に読まれる。
            Assert.True(opf.IndexOf("idref=\"cover\"", StringComparison.Ordinal)
                < opf.IndexOf("idref=\"chapter-001\"", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(outputPath);
            File.Delete(imagePath);
        }
    }

    [Fact]
    public void Build_表紙を指定しなければ表紙ページを作らない()
    {
        var block = new PageBlock
        {
            Id = "p0001-b0001",
            PageNumber = 1,
            Bounds = new BoundingBox(0, 0, 1, 1),
            Type = BlockType.Body,
            OcrText = "本文テキスト",
        };

        var chapter = new DocumentChapter { Id = "chapter-001", Title = "第1章" };
        chapter.BlockIds.Add(block.Id);

        var project = new EpubFabricProject
        {
            Id = Guid.NewGuid(),
            Title = "表紙なし書籍",
            SourcePdfPath = "dummy.pdf",
        };

        var outputPath = Path.Combine(Path.GetTempPath(), $"epubfabric-test-{Guid.NewGuid():N}.epub");

        try
        {
            new EpubPackageBuilder().Build(project, [chapter], new Dictionary<string, PageBlock> { [block.Id] = block }, outputPath);

            using var zip = ZipFile.OpenRead(outputPath);
            Assert.DoesNotContain("EPUB/text/cover.xhtml", zip.Entries.Select(e => e.FullName));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    /// <summary>
    /// 抽出した図版は元のページ解像度のまま収録されるため、上限がないとビューポートを
    /// 突き抜けて横スクロールが発生する（リフロー表示で画像の右側がはみ出す不具合の回帰）。
    /// </summary>
    [Fact]
    public void Build_図版が画面幅を超えないCSSを収録する()
    {
        var project = new EpubFabricProject
        {
            Id = Guid.NewGuid(),
            Title = "テスト書籍",
            SourcePdfPath = "dummy.pdf",
        };

        var outputPath = Path.Combine(Path.GetTempPath(), $"epubfabric-test-{Guid.NewGuid():N}.epub");

        try
        {
            new EpubPackageBuilder().Build(project, [], new Dictionary<string, PageBlock>(), outputPath);

            using var zip = ZipFile.OpenRead(outputPath);
            using var reader = new StreamReader(zip.GetEntry("EPUB/styles/book.css")!.Open());
            var css = reader.ReadToEnd().Replace(" ", "").Replace("\r", "").Replace("\n", "");

            Assert.Contains("img{max-width:100%;height:auto;}", css);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static string CreatePagePng()
    {
        var path = Path.Combine(Path.GetTempPath(), $"epubfabric-cover-{Guid.NewGuid():N}.png");
        using var bitmap = new SKBitmap(40, 60);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.OpenWrite(path);
        data.SaveTo(stream);
        return path;
    }

    [Fact]
    public void Build_StripsInvalidXmlCharactersFromOcrText()
    {
        // OCRはU+FFFEや不対サロゲート等、XML 1.0で許可されない文字を含むテキストを
        // 返すことがあり、無処理でXDocument.Saveに渡すとArgumentExceptionで変換全体が失敗する。
        var block = new PageBlock
        {
            Id = "p0001-b0001",
            PageNumber = 1,
            Bounds = new BoundingBox(0, 0, 1, 1),
            Type = BlockType.Body,
            OcrText = "前￾中\uD800後",
        };

        var chapter = new DocumentChapter { Id = "chapter-001", Title = "第￿1章" };
        chapter.BlockIds.Add(block.Id);

        var project = new EpubFabricProject
        {
            Id = Guid.NewGuid(),
            Title = "タイトル￾付き",
            SourcePdfPath = "dummy.pdf",
        };

        var outputPath = Path.Combine(Path.GetTempPath(), $"epubfabric-test-{Guid.NewGuid():N}.epub");

        try
        {
            new EpubPackageBuilder().Build(project, [chapter], new Dictionary<string, PageBlock> { [block.Id] = block }, outputPath);

            using var zip = ZipFile.OpenRead(outputPath);
            using var reader = new StreamReader(zip.GetEntry("EPUB/text/chapter-001.xhtml")!.Open());
            var xhtml = reader.ReadToEnd();

            Assert.Contains("前中後", xhtml);
            Assert.Contains("第1章", xhtml);

            using var navReader = new StreamReader(zip.GetEntry("EPUB/nav.xhtml")!.Open());
            Assert.Contains("タイトル付き", navReader.ReadToEnd());
        }
        finally
        {
            File.Delete(outputPath);
        }
    }
}
