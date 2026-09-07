using System.Text;
using EpubFabric.Core.Models;
using EpubFabric.Pipeline;

namespace EpubFabric.Tests;

public class NdlOcrXmlAdapterTests
{
    [Fact]
    public void Parse_NormalizesCoordinatesAndSortsBySourceOrder()
    {
        const string xml = """
            <OCRDATASET>
              <PAGE WIDTH="1000" HEIGHT="2000">
                <TEXTBLOCK>
                  <LINE TYPE="キャプション" X="100" Y="500" WIDTH="400" HEIGHT="40" CONF="0.91" ORDER="1" STRING="図の説明" />
                  <LINE TYPE="本文" X="800" Y="100" WIDTH="40" HEIGHT="600" CONF="0.95" ORDER="0" STRING="縦書き本文" />
                </TEXTBLOCK>
              </PAGE>
            </OCRDATASET>
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var result = NdlOcrXmlAdapter.Parse(stream);

        Assert.Equal(1000, result.ImageWidth);
        Assert.Equal(2000, result.ImageHeight);
        Assert.Equal(2, result.Lines.Count);

        var vertical = result.Lines[0];
        Assert.Equal("縦書き本文", vertical.Text);
        Assert.Equal(0.8, vertical.Bounds.X, precision: 10);
        Assert.Equal(0.05, vertical.Bounds.Y, precision: 10);
        Assert.Equal(0.04, vertical.Bounds.Width, precision: 10);
        Assert.Equal(0.3, vertical.Bounds.Height, precision: 10);
        Assert.Equal(TextSourceKind.NdlOcr, vertical.Source);
        Assert.Equal(WritingMode.Vertical, vertical.DetectedWritingMode);
        Assert.Equal(0, vertical.SourceReadingOrder);
        Assert.Equal("本文", vertical.SourceType);

        var caption = result.Lines[1];
        Assert.Equal(WritingMode.Horizontal, caption.DetectedWritingMode);
        Assert.Equal("キャプション", caption.SourceType);
        Assert.Equal(0.93, result.AverageConfidence, precision: 2);
        Assert.Equal(0.5, result.VerticalLineShare);
    }

    [Fact]
    public void Parse_RejectsDuplicateReadingOrder()
    {
        const string xml = """
            <OCRDATASET>
              <PAGE WIDTH="100" HEIGHT="100">
                <LINE X="1" Y="1" WIDTH="10" HEIGHT="20" ORDER="0" STRING="一" />
                <LINE X="20" Y="1" WIDTH="10" HEIGHT="20" ORDER="0" STRING="二" />
              </PAGE>
            </OCRDATASET>
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var exception = Assert.Throws<InvalidDataException>(() => NdlOcrXmlAdapter.Parse(stream));

        Assert.Contains("ORDERが重複", exception.Message);
    }

    [Fact]
    public void Parse_RejectsXmlWithoutTextLines()
    {
        const string xml = "<OCRDATASET><PAGE WIDTH=\"100\" HEIGHT=\"100\" /></OCRDATASET>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var exception = Assert.Throws<InvalidDataException>(() => NdlOcrXmlAdapter.Parse(stream));

        Assert.Contains("有効な文字行がありません", exception.Message);
    }
}
