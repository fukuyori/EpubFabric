using System.Text;
using EpubFabric.Core.Models;
using EpubFabric.Pipeline;

namespace EpubFabric.Tests;

public class PpDocLayoutJsonAdapterTests
{
    [Fact]
    public void Parse_NormalizesRegionsAndPreservesLabels()
    {
        const string json = """
            {
              "boxes": [
                {
                  "label": "image",
                  "score": 0.98,
                  "coordinate": [100, 200, 900, 1200],
                  "order": null
                },
                {
                  "label": "vertical_text",
                  "score": 0.94,
                  "coordinate": [800, 1300, 960, 1900],
                  "order": 2
                }
              ]
            }
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var regions = PpDocLayoutJsonAdapter.Parse(stream, imageWidth: 1000, imageHeight: 2000);

        Assert.Equal(2, regions.Count);
        Assert.Equal("image", regions[0].Label);
        Assert.Equal(new BoundingBox(0.1, 0.1, 0.8, 0.5), regions[0].Bounds);
        Assert.Null(regions[0].ReadingOrder);
        Assert.Equal("vertical_text", regions[1].Label);
        Assert.Equal(2, regions[1].ReadingOrder);
    }

    [Fact]
    public void Parse_RejectsEmptyCoordinates()
    {
        const string json = """
            {"boxes":[{"label":"image","score":0.9,"coordinate":[10,10,10,20],"order":null}]}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = Assert.Throws<InvalidDataException>(() =>
            PpDocLayoutJsonAdapter.Parse(stream, imageWidth: 100, imageHeight: 100));

        Assert.Contains("空の領域座標", exception.Message);
    }

    [Fact]
    public void Parse_RejectsMissingRequiredPropertyAsInvalidData()
    {
        const string json = """
            {"boxes":[{"score":0.9,"coordinate":[10,10,20,20],"order":null}]}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = Assert.Throws<InvalidDataException>(() =>
            PpDocLayoutJsonAdapter.Parse(stream, imageWidth: 100, imageHeight: 100));

        Assert.Contains("label", exception.Message);
    }

    [Fact]
    public void Parse_RejectsOutOfRangeConfidence()
    {
        const string json = """
            {"boxes":[{"label":"image","score":1.1,"coordinate":[10,10,20,20],"order":null}]}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var exception = Assert.Throws<InvalidDataException>(() =>
            PpDocLayoutJsonAdapter.Parse(stream, imageWidth: 100, imageHeight: 100));

        Assert.Contains("score", exception.Message);
    }
}
