using EpubFabric.Core.Models;
using EpubFabric.Pdf;

namespace EpubFabric.Tests;

public class PdfTextLineReconcilerTests
{
    [Fact]
    public void Reconcile_RestoresRawPdfSpacingWhileKeepingBounds()
    {
        var bounds = new BoundingBox(0.1, 0.2, 0.7, 0.03);
        var positioned = new[]
        {
            new TextLine(bounds, "This book has a lot ofknowledge in it.", 1, TextSourceKind.PdfTextLayer),
        };

        var result = PdfTextLineReconciler.Reconcile(positioned, "This book has a lot of knowledge in it.\n");

        var line = Assert.Single(result);
        Assert.Equal("This book has a lot of knowledge in it.", line.Text);
        Assert.Equal(bounds, line.Bounds);
    }

    [Fact]
    public void Reconcile_MergesSameRowSegmentsThatBelongToOneRawLine()
    {
        var positioned = new[]
        {
            new TextLine(new BoundingBox(0.1, 0.2, 0.5, 0.03), "correct me", 1, TextSourceKind.PdfTextLayer),
            new TextLine(new BoundingBox(0.7, 0.2, 0.2, 0.03), "butdon’ttryto", 1, TextSourceKind.PdfTextLayer),
        };

        var result = PdfTextLineReconciler.Reconcile(positioned, "correct me — but don’t try to\n");

        var line = Assert.Single(result);
        Assert.Equal("correct me — but don’t try to", line.Text);
        Assert.Equal(0.1, line.Bounds.X, precision: 6);
        Assert.Equal(0.2, line.Bounds.Y, precision: 6);
        Assert.Equal(0.8, line.Bounds.Width, precision: 6);
        Assert.Equal(0.03, line.Bounds.Height, precision: 6);
    }

    [Fact]
    public void Reconcile_NormalizesBrokenHyphenSpacingFromRawPdfText()
    {
        var positioned = new[]
        {
            new TextLine(new BoundingBox(0.1, 0.2, 0.7, 0.03), "single- character fixes.", 1),
        };

        var result = PdfTextLineReconciler.Reconcile(positioned, "single- character fixes.\n");

        Assert.Equal("single-character fixes.", Assert.Single(result).Text);
    }

    [Fact]
    public void Reconcile_NormalizesEnglishQuoteAndEmDashSpacing()
    {
        var positioned = new[]
        {
            new TextLine(new BoundingBox(0.1, 0.2, 0.7, 0.03), "‘art’and —after", 1),
        };

        var result = PdfTextLineReconciler.Reconcile(positioned, "‘art’and —after\n");

        Assert.Equal("‘art’ and — after", Assert.Single(result).Text);
    }

}
