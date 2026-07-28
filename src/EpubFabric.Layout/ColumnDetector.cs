using EpubFabric.Core.Models;

namespace EpubFabric.Layout;

/// <summary>
/// 座標だけを手がかりに多段組みの段構成を推定する。領域内に、どの行も跨がない
/// 縦の隙間（ガター）を探し、見つかったら左右に分割して再帰的に繰り返すことで、
/// 2段だけでなく3～4段組みや不等幅の段にも対応する。段をまたぐ幅広の項目
/// （大見出し・図など）はガター判定から除外して独立の「段」とし、Y座標順に
/// 他の段と並べる。ガターが見つからない場合は全体を1段として返す。
/// </summary>
public static class ColumnDetector
{
    private const double GutterStep = 0.01;

    /// <summary>分割後の各段が持つべき最小の項目割合。これ未満の偏った分割はガターとみなさない。
    /// 記事タイトルを縦組みで置く細いサイドバーは本文に対して1割にも満たないため、
    /// 割合の下限は低く取り、代わりに件数の下限（<see cref="MinColumnItems"/>）と
    /// 跨ぎ行の許容数で誤分割を防ぐ。</summary>
    private const double MinColumnShare = 0.05;

    /// <summary>分割後の各段が持つべき最小の項目数。1〜2件の孤立した行で段を作らせない。</summary>
    private const int MinColumnItems = 2;

    /// <summary>例外的に段をまたぐ項目（図中ラベル・キャプション等）を許容する最低数。</summary>
    private const int MinCrossingTolerance = 2;

    /// <summary>段をまたぐ項目の許容割合。本文が多いページでは図解由来のまたぎ行も増えるため行数比で許容する。</summary>
    private const double CrossingToleranceRatio = 0.08;

    /// <summary>領域幅に対してこの割合より広い項目は、段間の探索から除く（段をまたぐ要素とみなす）。</summary>
    private const double WideItemWidthRatio = 0.5;

    /// <summary>全項目のうちこの割合を超える数が跨ぐ位置は、段間ではないとみなす。</summary>
    private const double MaxCrossingShare = 0.35;

    /// <summary>再帰分割の最大深さ。2で最大4段まで検出できる。</summary>
    private const int MaxDepth = 2;

    /// <summary>ガターは領域の両端からこの割合だけ内側を探索する（余白をガターと誤認しないため）。
    /// 記事タイトルの縦組みなど、紙面の端に寄った細い段はここから外れると見つけられないため
    /// 浅めに取る。余白そのものは、片側に項目が残らず段の条件を満たさないので除外される。</summary>
    private const double GutterSearchInset = 0.10;

    /// <summary>これより項目が少ない領域は分割しない。</summary>
    private const int MinItemsToSplit = 4;

    /// <summary>
    /// 項目を読み順の段のリストに分割する。返り値の各段は読み順（左の段が先、
    /// ただし開始Y座標が小さい段が先）で並ぶ。段内の並べ替えは呼び出し側で行う。
    /// </summary>
    public static List<List<T>> DetectColumns<T>(IReadOnlyList<T> items, Func<T, BoundingBox> boundsOf)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var groups = Split(items.ToList(), boundsOf, 0.0, 1.0, 0)
            .Where(group => group.Count > 0)
            .ToList();

        SortGroupsIntoReadingOrder(groups, boundsOf);
        return groups;
    }

    /// <summary>
    /// 段グループを読み順に並べる。縦に重なり合う段同士（同じ帯の左右の段）は左から右へ、
    /// 重ならない帯同士（見出し帯と本文など）は上から下へ並べる。段の開始Y座標だけで
    /// 並べると、同じ高さから始まる段組みの左右順が僅かなY差で入れ替わってしまうため。
    /// 比較は推移的でないため、安定な挿入ソートで隣接関係を優先して整える。
    /// </summary>
    private static void SortGroupsIntoReadingOrder<T>(List<List<T>> groups, Func<T, BoundingBox> boundsOf)
    {
        var keys = groups
            .Select(g => (
                Group: g,
                MinX: g.Min(i => boundsOf(i).X),
                MinY: g.Min(i => boundsOf(i).Y),
                MaxY: g.Max(i => boundsOf(i).Y + boundsOf(i).Height)))
            .ToList();

        for (var i = 1; i < keys.Count; i++)
        {
            var current = keys[i];
            var j = i - 1;
            while (j >= 0 && CompareGroups(keys[j], current) > 0)
            {
                keys[j + 1] = keys[j];
                j--;
            }
            keys[j + 1] = current;
        }

        for (var i = 0; i < groups.Count; i++)
        {
            groups[i] = keys[i].Group;
        }
    }

    private static int CompareGroups<T>(
        (List<T> Group, double MinX, double MinY, double MaxY) a,
        (List<T> Group, double MinX, double MinY, double MaxY) b)
    {
        var overlap = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
        var smallerHeight = Math.Min(a.MaxY - a.MinY, b.MaxY - b.MinY);

        return overlap > smallerHeight * 0.5
            ? a.MinX.CompareTo(b.MinX)
            : a.MinY.CompareTo(b.MinY);
    }

    private static List<List<T>> Split<T>(List<T> items, Func<T, BoundingBox> boundsOf, double xStart, double xEnd, int depth)
    {
        var regionWidth = xEnd - xStart;
        if (depth >= MaxDepth || items.Count < MinItemsToSplit || regionWidth <= 0)
        {
            return [items];
        }

        // ガターの探索では、領域の半分を超える幅の項目（段をまたぐ大見出しや広告枠など）を
        // 除く。含めるとどの位置も「跨がれている」ことになり、段間を見つけられなくなる。
        var wideThreshold = regionWidth * WideItemWidthRatio;
        var narrowItems = items.Where(i => boundsOf(i).Width <= wideThreshold).ToList();
        if (narrowItems.Count < MinItemsToSplit)
        {
            return [items];
        }

        var gutter = FindWidestGutterBand(items, narrowItems, boundsOf, xStart, xEnd);
        if (gutter is null)
        {
            return [items];
        }

        // 振り分けは全項目に対して行う。幅が広いだけで実際には段間を跨がない項目
        // （1段組みページの本文行など）を切り離すと、読み順がばらばらになるため、
        // 独立させるのは本当に段間を跨ぐ項目だけにする。
        var gutterX = (gutter.Value.Start + gutter.Value.End) / 2;
        var crossing = items.Where(i => Crosses(boundsOf(i), gutterX)).ToList();
        var nonCrossing = items.Except(crossing).ToList();
        var left = nonCrossing.Where(i => Center(boundsOf(i)) < gutterX).ToList();
        var right = nonCrossing.Where(i => Center(boundsOf(i)) >= gutterX).ToList();

        var result = new List<List<T>>();
        result.AddRange(Split(left, boundsOf, xStart, gutterX, depth + 1));
        result.AddRange(Split(right, boundsOf, gutterX, xEnd, depth + 1));

        // 段をまたぐ項目は、大きな図と同様に個別の段として扱い、Y座標で他の段と並ぶ。
        foreach (var item in crossing)
        {
            result.Add([item]);
        }

        return result;
    }

    /// <summary>
    /// 有効なガター位置の連続区間のうち最も幅の広いものを返す。最初に見つかった位置
    /// ではなく最も広い空白帯を選ぶことで、字下げ等による偶然の隙間ではなく本来の
    /// 段間を選びやすくする。
    /// </summary>
    private static (double Start, double End)? FindWidestGutterBand<T>(
        List<T> items, List<T> narrowItems, Func<T, BoundingBox> boundsOf, double xStart, double xEnd)
    {
        var regionWidth = xEnd - xStart;
        var searchStart = xStart + regionWidth * GutterSearchInset;
        var searchEnd = xEnd - regionWidth * GutterSearchInset;

        (double Start, double End)? widest = null;
        double? bandStart = null;
        var bandEnd = 0.0;

        for (var x = searchStart; x <= searchEnd + 1e-9; x += GutterStep)
        {
            if (IsValidGutter(items, narrowItems, boundsOf, x))
            {
                bandStart ??= x;
                bandEnd = x;
                continue;
            }

            widest = WiderOf(widest, bandStart, bandEnd);
            bandStart = null;
        }

        return WiderOf(widest, bandStart, bandEnd);
    }

    private static (double Start, double End)? WiderOf((double Start, double End)? current, double? bandStart, double bandEnd)
    {
        if (bandStart is null)
        {
            return current;
        }

        var band = (Start: bandStart.Value, End: bandEnd);
        return current is null || band.End - band.Start > current.Value.End - current.Value.Start ? band : current;
    }

    /// <summary>
    /// ガター候補の妥当性を、細い項目と全項目の両方から見る。細い項目だけで判定すると、
    /// 1段組みページで段落末の短い行だけを左右に振り分けた偽の段間を、本文行が跨いでいる
    /// のに見逃してしまう。逆に全項目だけで判定すると、段をまたぐ広告枠や大見出しが数点
    /// あるだけで本物の段間を弾いてしまう。
    /// </summary>
    private static bool IsValidGutter<T>(List<T> items, List<T> narrowItems, Func<T, BoundingBox> boundsOf, double gutterX)
    {
        var allCrossingCount = items.Count(i => Crosses(boundsOf(i), gutterX));
        if (allCrossingCount > items.Count * MaxCrossingShare)
        {
            return false;
        }

        var crossingCount = 0;
        var leftCount = 0;
        var rightCount = 0;

        foreach (var item in narrowItems)
        {
            var bounds = boundsOf(item);
            if (Crosses(bounds, gutterX))
            {
                crossingCount++;
                continue;
            }

            if (Center(bounds) < gutterX)
            {
                leftCount++;
            }
            else
            {
                rightCount++;
            }
        }

        var tolerance = Math.Max(MinCrossingTolerance, (int)(narrowItems.Count * CrossingToleranceRatio));
        if (crossingCount > tolerance)
        {
            return false;
        }

        var nonCrossingCount = leftCount + rightCount;
        if (nonCrossingCount == 0)
        {
            // 左右に振り分けられる項目が1つもない位置は、空白帯ではなく判定材料がないだけ。
            // 割合の条件は0件同士でも成立してしまうため、ここで明示的に弾く。
            return false;
        }

        return leftCount >= MinColumnItems
            && rightCount >= MinColumnItems
            && leftCount >= nonCrossingCount * MinColumnShare
            && rightCount >= nonCrossingCount * MinColumnShare;
    }

    private static bool Crosses(BoundingBox bounds, double x) => bounds.X < x && bounds.X + bounds.Width > x;

    private static double Center(BoundingBox bounds) => bounds.X + bounds.Width / 2;
}
