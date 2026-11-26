using EpubFabric.Pipeline;

namespace EpubFabric.Tests;

using LineBox = NdlOcrXyCutReadingOrder.LineBox;

public sealed class NdlOcrXyCutReadingOrderTests
{
    [Fact]
    public void Solve_ReturnsEmpty_ForNoLines()
    {
        Assert.Empty(NdlOcrXyCutReadingOrder.Solve([]));
    }

    [Fact]
    public void Solve_RanksSingleLineFirst()
    {
        var ranks = NdlOcrXyCutReadingOrder.Solve([new LineBox(10, 10, 30, 400)]);

        Assert.Equal([0], ranks);
    }

    [Fact]
    public void Solve_OrdersVerticalColumnsRightToLeft()
    {
        // 縦書き1段組み。右端の列から左端の列へ向かって読む。
        var boxes = Columns(count: 5, top: 60, bottom: 900);

        var sorted = SortByReadingOrder(boxes);

        Assert.Equal(sorted.Select(box => box.X0).OrderDescending(), sorted.Select(box => box.X0));
    }

    [Fact]
    public void Solve_OrdersHorizontalLinesTopToBottom()
    {
        // 横書き1段組み。上の行から下の行へ読む。
        var boxes = new List<LineBox>
        {
            HorizontalLine(row: 2),
            HorizontalLine(row: 0),
            HorizontalLine(row: 1),
        };

        var sorted = SortByReadingOrder(boxes);

        Assert.Equal(sorted.Select(box => box.Y0).Order(), sorted.Select(box => box.Y0));
    }

    [Fact]
    public void Solve_ReadsUpperBandCompletelyBeforeLowerBand()
    {
        // 紙面が上下2つの記事帯に分かれ、それぞれが縦書きの複数列である場合。
        // 独自の帯分割では下の帯の行が上の帯の列の間へ割り込む誤りが出ていた。
        var boxes = new List<LineBox>();
        boxes.AddRange(Columns(count: 4, top: 40, bottom: 400));
        boxes.AddRange(Columns(count: 4, top: 560, bottom: 920));

        var sorted = SortByReadingOrder(boxes);

        var upper = sorted.Take(4).ToList();
        var lower = sorted.Skip(4).ToList();
        Assert.All(upper, box => Assert.Equal(40, box.Y0));
        Assert.All(lower, box => Assert.Equal(560, box.Y0));
        Assert.Equal(upper.Select(box => box.X0).OrderDescending(), upper.Select(box => box.X0));
        Assert.Equal(lower.Select(box => box.X0).OrderDescending(), lower.Select(box => box.X0));
    }

    [Fact]
    public void Solve_PlacesLeftFigureCaptionAfterRightBodyColumns()
    {
        // 縦書きページの右側に本文列、左下に図の横書きキャプションがある構成。
        // 縦書きが多数を占めるため、左右分割は右の領域から読む。
        var boxes = new List<LineBox>();
        boxes.AddRange(Columns(count: 8, top: 60, bottom: 900));
        var captions = new List<LineBox>
        {
            new(120, 700, 620, 730),
            new(120, 740, 620, 770),
        };
        boxes.AddRange(captions);

        var sorted = SortByReadingOrder(boxes);

        Assert.DoesNotContain(sorted.Take(8), box => captions.Contains(box));
        Assert.Equal(captions, sorted.Skip(8));
    }

    [Fact]
    public void Solve_OrdersHorizontalColumnsLeftToRight()
    {
        // 横書きが多数を占めるページでは、左右分割を左の領域から読む。
        var left = new List<LineBox>();
        var right = new List<LineBox>();
        for (var row = 0; row < 6; row++)
        {
            left.Add(new LineBox(100, 100 + (row * 60), 500, 130 + (row * 60)));
            right.Add(new LineBox(620, 100 + (row * 60), 1020, 130 + (row * 60)));
        }

        var sorted = SortByReadingOrder([.. left, .. right]);

        Assert.Equal(left, sorted.Take(6));
        Assert.Equal(right, sorted.Skip(6));
    }

    [Fact]
    public void Solve_AssignsEveryLineADistinctRank()
    {
        var boxes = new List<LineBox>();
        boxes.AddRange(Columns(count: 6, top: 60, bottom: 500));
        boxes.AddRange(Columns(count: 6, top: 560, bottom: 980));
        boxes.Add(new LineBox(200, 1010, 900, 1040));

        var ranks = NdlOcrXyCutReadingOrder.Solve(boxes);

        Assert.Equal(Enumerable.Range(0, boxes.Count), ranks.Order());
    }

    [Fact]
    public void Solve_HandlesZeroSizedBoxes()
    {
        // 幅または高さが0の検出結果でも例外にならず、全行へ順位が付く。
        var boxes = new List<LineBox>
        {
            new(100, 100, 100, 100),
            new(860, 60, 900, 900),
            new(300, 200, 300, 400),
        };

        var ranks = NdlOcrXyCutReadingOrder.Solve(boxes);

        Assert.Equal(boxes.Count, ranks.Length);
        Assert.DoesNotContain(-1, ranks);
    }

    [Fact]
    public void Solve_HandlesIdenticalBoxes()
    {
        var boxes = Enumerable.Repeat(new LineBox(100, 100, 140, 500), 3).ToList();

        var ranks = NdlOcrXyCutReadingOrder.Solve(boxes);

        Assert.Equal(Enumerable.Range(0, boxes.Count), ranks.Order());
    }

    [Fact]
    public void Overlap_IsOneWhenLineIsInsideLeaf()
    {
        Assert.Equal(1.0, NdlOcrXyCutReadingOrder.Overlap(new LineBox(10, 10, 20, 50), 0, 0, 100, 100));
    }

    [Fact]
    public void Overlap_IsZeroWhenDisjoint()
    {
        Assert.Equal(0.0, NdlOcrXyCutReadingOrder.Overlap(new LineBox(10, 10, 20, 50), 30, 0, 100, 100));
    }

    [Fact]
    public void Overlap_IsFractionOfLineArea()
    {
        // 行は x=0..9（両端含む10画素）。葉は x=5.. なので5画素分、高さは全て重なる。
        var overlap = NdlOcrXyCutReadingOrder.Overlap(new LineBox(0, 0, 9, 9), 5, 0, 100, 100);

        Assert.Equal(0.5, overlap, precision: 10);
    }

    [Fact]
    public void Overlap_DoesNotFavorSmallLeaves()
    {
        // 修正前の公式版の式は葉の大きさに左右された。行を丸ごと含む葉は、大きさにかかわらず1になる。
        var line = new LineBox(10, 10, 20, 50);

        Assert.Equal(
            NdlOcrXyCutReadingOrder.Overlap(line, 0, 0, 1000, 1000),
            NdlOcrXyCutReadingOrder.Overlap(line, 5, 5, 25, 55));
    }

    /// <summary>右端から左へ並ぶ縦書きの列を作る。</summary>
    private static List<LineBox> Columns(int count, int top, int bottom)
    {
        var columns = new List<LineBox>();
        for (var i = 0; i < count; i++)
        {
            var right = 900 - (i * 60);
            columns.Add(new LineBox(right - 40, top, right, bottom));
        }

        return columns;
    }

    /// <summary>横書きの1行。行番号が大きいほど下へ置く。</summary>
    private static LineBox HorizontalLine(int row) =>
        new(100, 100 + (row * 60), 800, 130 + (row * 60));

    /// <summary>読み順に並べ替えた矩形を返す。</summary>
    private static List<LineBox> SortByReadingOrder(IReadOnlyList<LineBox> boxes)
    {
        var ranks = NdlOcrXyCutReadingOrder.Solve(boxes);
        return Enumerable.Range(0, boxes.Count)
            .OrderBy(index => ranks[index])
            .Select(index => boxes[index])
            .ToList();
    }
}
