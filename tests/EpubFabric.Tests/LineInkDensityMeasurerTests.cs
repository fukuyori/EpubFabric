using EpubFabric.Core.Models;
using EpubFabric.Imaging;
using OpenCvSharp;

namespace EpubFabric.Tests;

public sealed class LineInkDensityMeasurerTests : IDisposable
{
    private readonly string _tempDirectory =
        Directory.CreateTempSubdirectory("epubfabric-inkdensity-").FullName;

    public void Dispose() => Directory.Delete(_tempDirectory, recursive: true);

    [Fact]
    public void 太い線の行は細い線の行よりインク密度が高い()
    {
        var imagePath = Path.Combine(_tempDirectory, "page.png");
        using (var page = new Mat(1000, 800, MatType.CV_8UC3, Scalar.All(255)))
        {
            // 太字相当: 100-160pxの帯に太い横線を3本
            for (var y = 110; y <= 150; y += 20)
            {
                Cv2.Rectangle(page, new Rect(100, y, 400, 10), Scalar.All(0), thickness: -1);
            }
            // 本文相当: 300-360pxの帯に細い横線を3本
            for (var y = 310; y <= 350; y += 20)
            {
                Cv2.Rectangle(page, new Rect(100, y, 400, 3), Scalar.All(0), thickness: -1);
            }
            Cv2.ImWrite(imagePath, page);
        }

        var lines = new[]
        {
            new TextLine(new BoundingBox(0.125, 0.10, 0.5, 0.06), "太字行", 0.9),
            new TextLine(new BoundingBox(0.125, 0.30, 0.5, 0.06), "本文行", 0.9),
        };

        var measured = new LineInkDensityMeasurer().Measure(imagePath, lines);

        Assert.NotNull(measured[0].InkDensity);
        Assert.NotNull(measured[1].InkDensity);
        Assert.True(
            measured[0].InkDensity > measured[1].InkDensity * 2,
            $"bold={measured[0].InkDensity:0.000} body={measured[1].InkDensity:0.000}");
    }

    [Fact]
    public void 画像が読めない場合は行をそのまま返す()
    {
        var lines = new[] { new TextLine(new BoundingBox(0.1, 0.1, 0.5, 0.05), "行", 0.9) };
        var measured = new LineInkDensityMeasurer().Measure(Path.Combine(_tempDirectory, "missing.png"), lines);

        Assert.Same(lines, measured);
        Assert.Null(lines[0].InkDensity);
    }
}
