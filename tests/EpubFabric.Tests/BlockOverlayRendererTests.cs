using EpubFabric.Core.Models;
using EpubFabric.Imaging;
using OpenCvSharp;

namespace EpubFabric.Tests;

public class BlockOverlayRendererTests
{
    [Fact]
    public void Render_DrawsBlocksAndLimitsOutputWidth()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"epubfabric-overlay-src-{Guid.NewGuid():N}.png");
        var outputPath = Path.Combine(Path.GetTempPath(), $"epubfabric-overlay-out-{Guid.NewGuid():N}.jpg");

        try
        {
            using (var blank = new Mat(3200, 2400, MatType.CV_8UC3, Scalar.White))
            {
                Cv2.ImWrite(sourcePath, blank);
            }

            var blocks = new List<PageBlock>
            {
                new()
                {
                    Id = "b1", PageNumber = 1, Bounds = new BoundingBox(0.1, 0.05, 0.8, 0.06),
                    Type = BlockType.SectionHeading, OcrText = "見出し", ReadingOrder = 0,
                },
                new()
                {
                    Id = "b2", PageNumber = 1, Bounds = new BoundingBox(0.1, 0.15, 0.38, 0.5),
                    Type = BlockType.Body, OcrText = "本文", ReadingOrder = 1,
                },
                new()
                {
                    Id = "b3", PageNumber = 1, Bounds = new BoundingBox(0.52, 0.15, 0.38, 0.3),
                    Type = BlockType.Figure, ReadingOrder = 2,
                },
                new()
                {
                    Id = "b4", PageNumber = 1, Bounds = new BoundingBox(0.1, 0.95, 0.8, 0.03),
                    Type = BlockType.Footer, OcrText = "柱", ReadingOrder = 3, IsExcluded = true,
                },
            };

            new BlockOverlayRenderer().Render(sourcePath, blocks, outputPath);

            using var rendered = Cv2.ImRead(outputPath, ImreadModes.Color);
            Assert.False(rendered.Empty());
            Assert.Equal(1200, rendered.Width);

            // 枠とラベルが描かれていれば、真っ白ではなくなっている。
            Cv2.MinMaxLoc(rendered.ExtractChannel(0), out double min, out _);
            Assert.True(min < 250);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }
}
