using System.IO.Compression;
using EpubFabric.Core.Models;
using EpubFabric.Epub;
using SkiaSharp;

namespace EpubFabric.Tests;

public class FixedLayoutEpubPackageBuilderTests
{
    [Fact]
    public void Build_LargePageImage_IsDownscaledAndRecompressedToJpeg()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"epubfabric-fixed-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var imagePath = Path.Combine(tempDirectory, "source.png");
        CreateLargePng(imagePath, width: 2600, height: 3600);

        var page = new DocumentPage
        {
            PageNumber = 1,
            OriginalImagePath = imagePath,
            ProcessedImagePath = imagePath,
            PreviewImagePath = imagePath,
            Width = 612,
            Height = 792,
        };
        var project = new EpubFabricProject
        {
            Id = Guid.NewGuid(),
            Title = "画像圧縮試験",
            SourcePdfPath = "source.pdf",
            Pages = [page],
        };
        var outputPath = Path.Combine(tempDirectory, "book.epub");

        try
        {
            new FixedLayoutEpubPackageBuilder(jpegQuality: 85, maxImageSideLength: 2200).Build(project, outputPath);

            using var zip = ZipFile.OpenRead(outputPath);
            var imageEntry = zip.GetEntry("EPUB/images/page-0001.jpg");
            Assert.NotNull(imageEntry);

            using var stream = new MemoryStream();
            using (var entryStream = imageEntry.Open())
            {
                entryStream.CopyTo(stream);
            }

            var bounds = SKBitmap.DecodeBounds(stream.ToArray());
            Assert.Equal(2200, Math.Max(bounds.Width, bounds.Height));

            // XHTMLはjpg版の画像を参照し、キャンバス寸法はPDFポイントのまま（座標へ影響しない）。
            var xhtml = ReadEntry(zip, "EPUB/text/page-0001.xhtml");
            Assert.Contains("../images/page-0001.jpg", xhtml);
            Assert.Contains("width:612px;height:792px", xhtml);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void Build_MaxImageSizeZero_KeepsOriginalResolution()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"epubfabric-fixed-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var imagePath = Path.Combine(tempDirectory, "source.png");
        CreateLargePng(imagePath, width: 2600, height: 3600);

        var page = new DocumentPage
        {
            PageNumber = 1,
            OriginalImagePath = imagePath,
            ProcessedImagePath = imagePath,
            PreviewImagePath = imagePath,
            Width = 612,
            Height = 792,
        };
        var project = new EpubFabricProject
        {
            Id = Guid.NewGuid(),
            Title = "画像無制限試験",
            SourcePdfPath = "source.pdf",
            Pages = [page],
        };
        var outputPath = Path.Combine(tempDirectory, "book.epub");

        try
        {
            new FixedLayoutEpubPackageBuilder(jpegQuality: 85, maxImageSideLength: 0).Build(project, outputPath);

            using var zip = ZipFile.OpenRead(outputPath);
            var imageEntry = zip.GetEntry("EPUB/images/page-0001.jpg");
            Assert.NotNull(imageEntry);

            using var stream = new MemoryStream();
            using (var entryStream = imageEntry.Open())
            {
                entryStream.CopyTo(stream);
            }

            // 上限なしでは縮小されない（大きなPNGのJPEG化のみ行われる）。
            var bounds = SKBitmap.DecodeBounds(stream.ToArray());
            Assert.Equal(3600, Math.Max(bounds.Width, bounds.Height));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    /// <summary>写真的なノイズを含む大きなPNGを作る（一様な塗りだと数KBに縮み、再圧縮条件に入らないため）。</summary>
    private static void CreateLargePng(string path, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        var random = new Random(12345);
        for (var y = 0; y < height; y += 8)
        {
            for (var x = 0; x < width; x += 8)
            {
                var color = new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                for (var dy = 0; dy < 8 && y + dy < height; dy++)
                {
                    for (var dx = 0; dx < 8 && x + dx < width; dx++)
                    {
                        bitmap.SetPixel(x + dx, y + dy, color);
                    }
                }
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

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
            TextSource = TextSourceKind.PdfTextLayer,
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
            Assert.Contains("content=\"width=612, height=792\"", xhtml);
            Assert.Contains("<body style=\"width:612px;height:792px\"", xhtml);
            Assert.Contains("class=\"page-container\" style=\"width:612px;height:792px\"", xhtml);
            Assert.Contains("width=\"612\" height=\"792\"", xhtml);
            Assert.Contains("../images/page-0001.png", xhtml);
            Assert.Contains("PDFまたはOCRの文字", xhtml);
            Assert.Contains("data-text-source=\"pdf\"", xhtml);
            Assert.Contains("left:61.2px", xhtml);
            Assert.Contains("top:158.4px", xhtml);

            var css = ReadEntry(zip, "EPUB/styles/fixed-layout.css");
            Assert.Contains("color: transparent", css);
            Assert.Contains("object-fit: contain", css);
            Assert.DoesNotContain("object-fit: fill", css);
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
