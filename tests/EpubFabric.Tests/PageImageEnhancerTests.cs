using EpubFabric.Imaging;
using OpenCvSharp;

namespace EpubFabric.Tests;

public sealed class PageImageEnhancerTests : IDisposable
{
    private readonly string _tempDirectory =
        Directory.CreateTempSubdirectory("epubfabric-enhance-").FullName;

    public void Dispose() => Directory.Delete(_tempDirectory, recursive: true);

    [Fact]
    public void くすんだ紙面は白色化され文字は濃いまま残る()
    {
        var originalPath = Path.Combine(_tempDirectory, "scan.png");
        var enhancedPath = Path.Combine(_tempDirectory, "enhanced.png");

        // くすんだ紙（205前後）+ 濃い文字（60）+ 裏写り（225前後の薄い文字）を模した紙面。
        using (var page = new Mat(1000, 800, MatType.CV_8UC3, new Scalar(200, 203, 205)))
        {
            for (var y = 100; y < 900; y += 50)
            {
                Cv2.Rectangle(page, new Rect(80, y, 500, 20), Scalar.All(60), thickness: -1);
            }
            Cv2.Rectangle(page, new Rect(80, 940, 640, 20), Scalar.All(190), thickness: -1); // 裏写り相当
            Cv2.ImWrite(originalPath, page);
        }

        var result = new PageImageEnhancer().Enhance(originalPath, enhancedPath);

        Assert.True(result.Applied);
        Assert.Equal(enhancedPath, result.ImagePath);

        using var enhanced = Cv2.ImRead(enhancedPath, ImreadModes.Grayscale);
        // 紙: ほぼ白へ / 裏写り: 白化 / 文字: 濃いまま
        Assert.True(enhanced.At<byte>(50, 400) >= 250, $"紙の輝度が低い: {enhanced.At<byte>(50, 400)}");
        Assert.True(enhanced.At<byte>(950, 400) >= 250, $"裏写りが白化されていない: {enhanced.At<byte>(950, 400)}");
        Assert.True(enhanced.At<byte>(110, 300) <= 80, $"文字が薄くなった: {enhanced.At<byte>(110, 300)}");
    }

    [Fact]
    public void 幾何は変わらない()
    {
        var originalPath = Path.Combine(_tempDirectory, "scan.png");
        var enhancedPath = Path.Combine(_tempDirectory, "enhanced.png");
        using (var page = new Mat(600, 500, MatType.CV_8UC3, new Scalar(200, 203, 205)))
        {
            Cv2.Rectangle(page, new Rect(50, 100, 300, 30), Scalar.All(50), thickness: -1);
            Cv2.ImWrite(originalPath, page);
        }

        var result = new PageImageEnhancer().Enhance(originalPath, enhancedPath);

        Assert.True(result.Applied);
        using var enhanced = Cv2.ImRead(enhancedPath);
        Assert.Equal(500, enhanced.Width);
        Assert.Equal(600, enhanced.Height);
    }

    [Fact]
    public void 暗い表紙ページは加工しない()
    {
        var originalPath = Path.Combine(_tempDirectory, "cover.png");
        var enhancedPath = Path.Combine(_tempDirectory, "enhanced.png");
        using (var cover = new Mat(1000, 800, MatType.CV_8UC3, new Scalar(60, 80, 120)))
        {
            Cv2.Rectangle(cover, new Rect(100, 100, 600, 100), new Scalar(240, 240, 240), thickness: -1);
            Cv2.ImWrite(originalPath, cover);
        }

        var result = new PageImageEnhancer().Enhance(originalPath, enhancedPath);

        Assert.False(result.Applied);
        Assert.Equal(originalPath, result.ImagePath);
        Assert.False(File.Exists(enhancedPath));
    }

    [Fact]
    public void 白背景の生成PDFページはほぼ変化しない()
    {
        var originalPath = Path.Combine(_tempDirectory, "digital.png");
        var enhancedPath = Path.Combine(_tempDirectory, "enhanced.png");
        using (var page = new Mat(1000, 800, MatType.CV_8UC3, Scalar.All(255)))
        {
            Cv2.Rectangle(page, new Rect(80, 100, 500, 20), Scalar.All(0), thickness: -1);
            // 中間調の図版（グレー120）: 白化しきい値より暗いので変化しないはず。
            Cv2.Rectangle(page, new Rect(80, 400, 300, 200), Scalar.All(120), thickness: -1);
            Cv2.ImWrite(originalPath, page);
        }

        var result = new PageImageEnhancer().Enhance(originalPath, enhancedPath);

        using var enhanced = Cv2.ImRead(result.ImagePath, ImreadModes.Grayscale);
        Assert.InRange(enhanced.At<byte>(450, 200), 100, 140); // 図版の中間調が保たれる
        Assert.True(enhanced.At<byte>(110, 300) <= 20);
        Assert.True(enhanced.At<byte>(50, 400) >= 250);
    }
}
