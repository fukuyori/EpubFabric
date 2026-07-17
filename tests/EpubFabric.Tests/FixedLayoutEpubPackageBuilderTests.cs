using System.IO.Compression;
using EpubFabric.Core.Models;
using EpubFabric.Epub;

namespace EpubFabric.Tests;

public class FixedLayoutEpubPackageBuilderTests
{
    [Fact]
    public void Build_WritesOneFixedLayoutDocumentAndImagePerPdfPage()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"epubfabric-fixed-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var imagePath = Path.Combine(tempDirectory, "source.png");
        File.WriteAllBytes(imagePath, OnePixelPng);

        var page = new DocumentPage
        {
            PageNumber = 1,
            OriginalImagePath = imagePath,
            ProcessedImagePath = imagePath,
            PreviewImagePath = imagePath,
            Width = 612,
            Height = 792,
        };
        page.Blocks.Add(new PageBlock
        {
            Id = "p0001-b0001",
            PageNumber = 1,
            Bounds = new BoundingBox(0.1, 0.2, 0.5, 0.05),
            Type = BlockType.Body,
            OcrText = "PDFまたはOCRの文字",
            ReadingOrder = 0,
        });

        var project = new EpubFabricProject
        {
            Id = Guid.NewGuid(),
            Title = "固定レイアウト試験",
            SourcePdfPath = "source.pdf",
            Pages = [page],
        };
        var outputPath = Path.Combine(tempDirectory, "book.epub");

        try
        {
            new FixedLayoutEpubPackageBuilder().Build(project, outputPath);

            using var zip = ZipFile.OpenRead(outputPath);
            var entries = zip.Entries.Select(e => e.FullName).ToList();

            Assert.Equal("mimetype", entries[0]);
            Assert.Equal(zip.GetEntry("mimetype")!.Length, zip.GetEntry("mimetype")!.CompressedLength);
            Assert.Contains("EPUB/text/page-0001.xhtml", entries);
            Assert.Contains("EPUB/images/page-0001.png", entries);
            Assert.Contains("EPUB/styles/fixed-layout.css", entries);

            var package = ReadEntry(zip, "EPUB/package.opf");
            Assert.Contains("<meta property=\"rendition:layout\">pre-paginated</meta>", package);
            Assert.Contains("page-progression-direction=\"ltr\"", package);

            var xhtml = ReadEntry(zip, "EPUB/text/page-0001.xhtml");
            Assert.Contains("content=\"width=612,height=792\"", xhtml);
            Assert.Contains("../images/page-0001.png", xhtml);
            Assert.Contains("PDFまたはOCRの文字", xhtml);
            Assert.Contains("left:61.2px", xhtml);
            Assert.Contains("top:158.4px", xhtml);

            var css = ReadEntry(zip, "EPUB/styles/fixed-layout.css");
            Assert.Contains("color: transparent", css);
            Assert.DoesNotContain("display: none", css);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Build_UsesCorrectedTextAndSkipsExcludedText()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"epubfabric-fixed-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var imagePath = Path.Combine(tempDirectory, "source.png");
        File.WriteAllBytes(imagePath, OnePixelPng);

        var page = new DocumentPage
        {
            PageNumber = 1,
            OriginalImagePath = imagePath,
            ProcessedImagePath = imagePath,
            PreviewImagePath = imagePath,
            Width = 100,
            Height = 100,
        };
        page.Blocks.Add(new PageBlock
        {
            Id = "corrected",
            PageNumber = 1,
            Bounds = new BoundingBox(0, 0, 1, 0.1),
            Type = BlockType.Body,
            OcrText = "修正前",
            CorrectedText = "修正後",
        });
        page.Blocks.Add(new PageBlock
        {
            Id = "excluded",
            PageNumber = 1,
            Bounds = new BoundingBox(0, 0.2, 1, 0.1),
            Type = BlockType.PageNumber,
            OcrText = "除外文字",
            IsExcluded = true,
        });

        var project = new EpubFabricProject
        {
            Id = Guid.NewGuid(),
            Title = "校正試験",
            SourcePdfPath = "source.pdf",
            Pages = [page],
        };
        var outputPath = Path.Combine(tempDirectory, "book.epub");

        try
        {
            new FixedLayoutEpubPackageBuilder().Build(project, outputPath);

            using var zip = ZipFile.OpenRead(outputPath);
            var xhtml = ReadEntry(zip, "EPUB/text/page-0001.xhtml");

            Assert.Contains("修正後", xhtml);
            Assert.DoesNotContain("修正前", xhtml);
            Assert.DoesNotContain("除外文字", xhtml);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static string ReadEntry(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
}
