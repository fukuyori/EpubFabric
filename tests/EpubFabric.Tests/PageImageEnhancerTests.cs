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

    /// <summary>
    /// 明るさだけを見て白化すると、淡い色の図版（薄い水色の塗りなど）が紙と一緒に
    /// 白へ流れて消える。彩度と色味の条件で対象を地色に限定していることの回帰。
    /// </summary>
    [Fact]
    public void 淡い色の図版は白化されない()
    {
        var originalPath = Path.Combine(_tempDirectory, "scan.png");
        var enhancedPath = Path.Combine(_tempDirectory, "enhanced.png");

        // 紙が既にほぼ白い紙面。ホワイトバランスの倍率が小さいため、白化の判定が結果を分ける。
        using (var page = new Mat(1000, 800, MatType.CV_8UC3, new Scalar(245, 247, 248)))
        {
            for (var y = 100; y < 700; y += 50)
            {
                Cv2.Rectangle(page, new Rect(80, y, 500, 20), Scalar.All(60), thickness: -1);
            }

            // 淡い水色の塗り。輝度は白化の対象域だが色が付いている。
            // 紙色の推定（上側95%点）が塗りに引きずられないよう、面積は紙面の5%未満にする。
            Cv2.Rectangle(page, new Rect(80, 800, 300, 80), new Scalar(252, 240, 222), thickness: -1);
            Cv2.ImWrite(originalPath, page);
        }

        var result = new PageImageEnhancer().Enhance(originalPath, enhancedPath);

        Assert.True(result.Applied);
        using var enhanced = Cv2.ImRead(enhancedPath, ImreadModes.Color);
        var tint = enhanced.At<Vec3b>(840, 200);

        // 白へ潰れず、青みが残っていること。
        Assert.True(tint.Item0 - tint.Item2 >= 20, $"色が失われた: B={tint.Item0} G={tint.Item1} R={tint.Item2}");
    }

    /// <summary>
    /// 口絵・全面写真のように紙色を正しく測れないページが混じっても、書籍全体の
    /// 代表紙色が引きずられないこと（中央絶対偏差による外れ値ページの除外）。
    /// </summary>
    [Fact]
    public void 書籍全体の紙色は外れページに引きずられない()
    {
        var pages = new List<PageEnhanceStats>
        {
            new(242, 0.80, 240, 242, 243),
            new(243, 0.82, 241, 243, 244),
            new(241, 0.78, 239, 241, 242),
            new(244, 0.85, 242, 244, 245),
            // 紙色が極端に暗く測れたページ（全面写真の紙面など）。
            new(190, 0.30, 150, 170, 185),
        };

        var profile = PageImageEnhancer.BuildProfile(pages);

        Assert.NotNull(profile);
        Assert.InRange(profile!.PaperLuminance, 241, 244);
        Assert.InRange(profile.PaperBlue, 239, 242);
    }

    [Fact]
    public void 紙面と呼べるページが少なければ書籍全体の紙色を決めない()
    {
        var pages = new List<PageEnhanceStats>
        {
            new(120, 0.10, 100, 110, 120),
            new(240, 0.80, 238, 240, 241),
        };

        Assert.Null(PageImageEnhancer.BuildProfile(pages));
    }

    /// <summary>
    /// 同じ紙の2ページは、片方に写真が多くても同じ強さで補正されること。
    /// ページごとに紙色を推定していた頃は、ここで明るさが揃わなかった。
    /// </summary>
    [Fact]
    public void 書籍全体の紙色を使うと隣り合うページの明るさが揃う()
    {
        var plainPath = Path.Combine(_tempDirectory, "plain.png");
        var withPhotoPath = Path.Combine(_tempDirectory, "photo.png");
        var enhancedPlainPath = Path.Combine(_tempDirectory, "plain-enhanced.png");
        var enhancedPhotoPath = Path.Combine(_tempDirectory, "photo-enhanced.png");

        // 同じ紙（B=205, G=208, R=210）の2ページ。片方には大きな灰色の写真がある。
        CreatePage(plainPath, addPhoto: false);
        CreatePage(withPhotoPath, addPhoto: true);

        var enhancer = new PageImageEnhancer();
        var profile = PageImageEnhancer.BuildProfile(
        [
            enhancer.Analyze(plainPath),
            enhancer.Analyze(withPhotoPath),
            enhancer.Analyze(plainPath),
        ]);

        Assert.NotNull(profile);
        var plain = enhancer.Enhance(plainPath, enhancedPlainPath, profile);
        var photo = enhancer.Enhance(withPhotoPath, enhancedPhotoPath, profile);

        Assert.True(plain.Applied);
        Assert.True(photo.Applied);

        using var enhancedPlain = Cv2.ImRead(enhancedPlainPath, ImreadModes.Grayscale);
        using var enhancedPhoto = Cv2.ImRead(enhancedPhotoPath, ImreadModes.Grayscale);

        // 紙の部分の明るさが2ページでほぼ同じになること。
        var plainPaper = enhancedPlain.At<byte>(30, 400);
        var photoPaper = enhancedPhoto.At<byte>(30, 400);
        Assert.InRange(Math.Abs(plainPaper - photoPaper), 0, 2);

        static void CreatePage(string path, bool addPhoto)
        {
            using var page = new Mat(1000, 800, MatType.CV_8UC3, new Scalar(205, 208, 210));
            for (var y = 100; y < 900; y += 50)
            {
                Cv2.Rectangle(page, new Rect(80, y, 500, 20), Scalar.All(60), thickness: -1);
            }

            if (addPhoto)
            {
                Cv2.Rectangle(page, new Rect(100, 300, 600, 400), Scalar.All(140), thickness: -1);
            }

            Cv2.ImWrite(path, page);
        }
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
