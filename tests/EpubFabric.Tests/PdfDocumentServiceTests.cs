using EpubFabric.Pdf;

namespace EpubFabric.Tests;

public class PdfDocumentServiceTests
{
    private static readonly string SamplePdfPath = Path.Combine(AppContext.BaseDirectory, "Assets", "sample-text.pdf");

    [Fact]
    public void GetInfo_ReturnsPageCountAndTextLayerFlag()
    {
        var service = new PdfDocumentService();

        var info = service.GetInfo(SamplePdfPath);

        Assert.Equal(1, info.PageCount);
        Assert.True(info.HasTextLayer);
        Assert.Single(info.Pages);
        Assert.True(info.Pages[0].HasText);
    }

    [Fact]
    public void ExtractPageText_ReturnsPageContent()
    {
        var service = new PdfDocumentService();

        var text = service.ExtractPageText(SamplePdfPath, pageNumber: 1);

        Assert.Contains("Hello EpubFabric", text);
    }

    [Fact]
    public void RenderPageToPng_WritesNonEmptyPngFile()
    {
        var service = new PdfDocumentService();
        var outputPath = Path.Combine(Path.GetTempPath(), $"epubfabric-test-{Guid.NewGuid():N}.png");

        try
        {
            service.RenderPageToPng(SamplePdfPath, pageNumber: 1, outputPath, dpi: 150);

            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 0);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void GetInfo_UnknownFile_ThrowsPdfLoadException()
    {
        var service = new PdfDocumentService();

        Assert.Throws<PdfLoadException>(() => service.GetInfo(Path.Combine(Path.GetTempPath(), "does-not-exist.pdf")));
    }
}
