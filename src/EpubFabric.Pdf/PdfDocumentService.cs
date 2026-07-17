using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;
using OpenCvSharp;

namespace EpubFabric.Pdf;

/// <summary>
/// 9.1 PDF読み込みと9.2 ページ画像化を担当する。
/// </summary>
public sealed class PdfDocumentService
{
    private readonly IDocLib _docLib = DocLib.Instance;

    public PdfDocumentInfo GetInfo(string pdfPath, string? password = null)
    {
        using var reader = OpenReader(pdfPath, password, dpi: 72);

        var pageCount = reader.GetPageCount();
        var pages = new List<PdfPageInfo>(pageCount);
        var hasTextLayer = false;

        for (var i = 0; i < pageCount; i++)
        {
            using var page = reader.GetPageReader(i);
            var pageHasText = !string.IsNullOrWhiteSpace(page.GetText());
            hasTextLayer |= pageHasText;

            pages.Add(new PdfPageInfo(
                PageNumber: i + 1,
                WidthPoints: page.GetPageWidth(),
                HeightPoints: page.GetPageHeight(),
                HasText: pageHasText));
        }

        return new PdfDocumentInfo(reader.GetPdfVersion().ToString() ?? "unknown", pageCount, hasTextLayer, pages);
    }

    public string ExtractPageText(string pdfPath, int pageNumber, string? password = null)
    {
        using var reader = OpenReader(pdfPath, password, dpi: 72);
        using var page = reader.GetPageReader(pageNumber - 1);
        return page.GetText();
    }

    /// <summary>
    /// ページを指定dpiでPNGへラスタライズする（page-original相当、8.2）。
    /// </summary>
    public void RenderPageToPng(string pdfPath, int pageNumber, string outputPath, int dpi = 300, string? password = null)
    {
        using var reader = OpenReader(pdfPath, password, dpi);
        using var page = reader.GetPageReader(pageNumber - 1);

        var width = page.GetPageWidth();
        var height = page.GetPageHeight();
        var bgra = page.GetImage();

        using var mat = Mat.FromPixelData(height, width, MatType.CV_8UC4, bgra);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Cv2.ImWrite(outputPath, mat);
    }

    private IDocReader OpenReader(string pdfPath, string? password, int dpi)
    {
        try
        {
            var scale = dpi / 72.0;
            var dimensions = new PageDimensions(scale);

            return password is null
                ? _docLib.GetDocReader(pdfPath, dimensions)
                : _docLib.GetDocReader(pdfPath, password, dimensions);
        }
        catch (Exception ex)
        {
            throw new PdfLoadException($"PDFを開けませんでした（暗号化されている可能性があります）: {pdfPath}", ex);
        }
    }
}
