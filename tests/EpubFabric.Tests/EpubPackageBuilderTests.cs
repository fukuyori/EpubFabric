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
}
