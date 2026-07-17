using System.IO.Compression;
using EpubFabric.Core.Models;
using EpubFabric.Document;
using EpubFabric.Epub;

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
