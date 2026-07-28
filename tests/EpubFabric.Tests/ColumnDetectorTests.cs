using EpubFabric.Core.Models;
using EpubFabric.Layout;

namespace EpubFabric.Tests;

public sealed class ColumnDetectorTests
{
    private sealed record Item(string Name, BoundingBox Bounds);

    private static List<string> Order(IEnumerable<Item> items) =>
        ColumnDetector.DetectColumns(items.ToList(), i => i.Bounds)
            .SelectMany(column => column.OrderBy(i => i.Bounds.Y))
            .Select(i => i.Name)
            .ToList();

    [Fact]
    public void 二段組みは左段を読み切ってから右段へ進む()
    {
        var items = new List<Item>
        {
            new("右1", new BoundingBox(0.55, 0.10, 0.35, 0.03)),
            new("左1", new BoundingBox(0.05, 0.10, 0.35, 0.03)),
            new("右2", new BoundingBox(0.55, 0.14, 0.35, 0.03)),
            new("左2", new BoundingBox(0.05, 0.14, 0.35, 0.03)),
            new("左3", new BoundingBox(0.05, 0.18, 0.35, 0.03)),
            new("右3", new BoundingBox(0.55, 0.18, 0.35, 0.03)),
        };

        Assert.Equal(["左1", "左2", "左3", "右1", "右2", "右3"], Order(items));
    }

    [Fact]
    public void 三段組みは左から順に段ごとに読む()
    {
        var items = new List<Item>();
        var starts = new[] { (X: 0.05, Name: "左"), (X: 0.37, Name: "中"), (X: 0.69, Name: "右") };
        foreach (var (x, name) in starts)
        {
            for (var row = 0; row < 3; row++)
            {
                items.Add(new Item($"{name}{row + 1}", new BoundingBox(x, 0.10 + row * 0.04, 0.26, 0.03)));
            }
        }

        Assert.Equal(["左1", "左2", "左3", "中1", "中2", "中3", "右1", "右2", "右3"], Order(items));
    }

    [Fact]
    public void 一段組みはY座標順のまま()
    {
        var items = new List<Item>
        {
            new("2行目", new BoundingBox(0.1, 0.20, 0.8, 0.03)),
            new("1行目", new BoundingBox(0.1, 0.10, 0.8, 0.03)),
            new("3行目", new BoundingBox(0.1, 0.30, 0.8, 0.03)),
        };

        Assert.Equal(["1行目", "2行目", "3行目"], Order(items));
    }

    [Fact]
    public void 段をまたぐ大見出しは位置に応じて独立して並ぶ()
    {
        var items = new List<Item>
        {
            new("大見出し", new BoundingBox(0.05, 0.05, 0.85, 0.04)),
            new("左1", new BoundingBox(0.05, 0.15, 0.35, 0.03)),
            new("左2", new BoundingBox(0.05, 0.19, 0.35, 0.03)),
            new("右1", new BoundingBox(0.55, 0.15, 0.35, 0.03)),
            new("右2", new BoundingBox(0.55, 0.19, 0.35, 0.03)),
        };

        Assert.Equal(["大見出し", "左1", "左2", "右1", "右2"], Order(items));
    }

    [Fact]
    public void 不等幅の段も検出できる()
    {
        // 本文段（広い）+ サイドバー（狭い）: ガターは中央から外れたX=0.6付近。
        var items = new List<Item>
        {
            new("本文1", new BoundingBox(0.05, 0.10, 0.45, 0.03)),
            new("本文2", new BoundingBox(0.05, 0.14, 0.45, 0.03)),
            new("本文3", new BoundingBox(0.05, 0.18, 0.45, 0.03)),
            new("側1", new BoundingBox(0.70, 0.10, 0.25, 0.03)),
            new("側2", new BoundingBox(0.70, 0.14, 0.25, 0.03)),
        };

        Assert.Equal(["本文1", "本文2", "本文3", "側1", "側2"], Order(items));
    }

    [Fact]
    public void 空リストは空を返す()
    {
        Assert.Empty(ColumnDetector.DetectColumns(new List<Item>(), i => i.Bounds));
    }

    /// <summary>
    /// 雑誌本文のように行数が多く、各行が段の幅いっぱいに伸びる2段組み。段に分割した後、
    /// 段の中の行は「その段の幅の半分」を超えるため、再帰の2段目で段をまたぐ幅広項目と
    /// 誤判定されて1行ずつのグループへ解体され、読み順が左右交互に崩れる不具合があった。
    /// </summary>
    [Fact]
    public void 行が段の幅いっぱいの二段組みでも左右が交互にならない()
    {
        var items = new List<Item>();
        for (var row = 0; row < 40; row++)
        {
            var y = 0.06 + row * 0.02;
            items.Add(new Item($"左{row + 1}", new BoundingBox(0.07, y, 0.41, 0.015)));
            items.Add(new Item($"右{row + 1}", new BoundingBox(0.51, y, 0.41, 0.015)));
        }

        var expected = Enumerable.Range(1, 40).Select(i => $"左{i}")
            .Concat(Enumerable.Range(1, 40).Select(i => $"右{i}"))
            .ToList();

        Assert.Equal(expected, Order(items));
    }

    /// <summary>
    /// 段落末尾や小見出しのように段の幅より短い行が段内に混ざっていても、段の中を
    /// さらに分割してはならない（短い行だけで再分割の条件が成立してしまう事故の回帰）。
    /// </summary>
    [Fact]
    public void 段内に短い行が混ざっても段はさらに分割されない()
    {
        var items = new List<Item>();
        for (var row = 0; row < 20; row++)
        {
            var y = 0.06 + row * 0.04;

            // 5行ごとに段落末尾の短い行を置く。
            var leftWidth = row % 5 == 4 ? 0.18 : 0.41;
            var rightWidth = row % 5 == 2 ? 0.15 : 0.41;
            items.Add(new Item($"左{row + 1}", new BoundingBox(0.07, y, leftWidth, 0.015)));
            items.Add(new Item($"右{row + 1}", new BoundingBox(0.51, y, rightWidth, 0.015)));
        }

        var groups = ColumnDetector.DetectColumns(items, i => i.Bounds);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Equal(20, group.Count));
    }
}
