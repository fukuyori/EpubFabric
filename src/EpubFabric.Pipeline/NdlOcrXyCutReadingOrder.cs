namespace EpubFabric.Pipeline;

// C# adaptation of reading_order/xy_cut/block_xy_cut.py from
// ndl-lab/ndlocr-lite (unchanged between d25e0d4 and v2.0.0 ba8bb85).
// NDLOCR-Lite: Copyright (c) 2023 National Diet Library, Japan, CC BY 4.0.
// The algorithm (grid normalization, recursive XY cut, node sorting,
// depth-first ranking) follows the original. This adaptation replaces NumPy
// array operations with scalar loops, adds an integral image so each cut reads
// row and column sums in linear time, guards the degenerate cases that NumPy
// would resolve as inf or NaN, and fixes two upstream defects: the leaf
// assignment measure (calc_iou) and the leaf sort key (is_vertical).

/// <summary>
/// NDLOCR-Liteの再帰XY切断による読み順決定。
/// </summary>
/// <remarks>
/// 公式版から次の2点を修正している。そのため、公式版の<c>ORDER</c>とは一致しない場合がある。
/// <list type="bullet">
/// <item>葉への割り当て: 公式版の<c>calc_iou</c>は座標を取り違えた式のため、
/// 行が葉に含まれる割合（<see cref="Overlap(LineBox,int,int,int,int)"/>）で判定する。</item>
/// <item>葉の中の並べ替え: 公式版は<c>node.is_vertical</c>をメソッド呼び出しせずに評価するため
/// 常に縦組みの順（右から左）になる。ここでは節の行の過半が縦長かどうかで縦組みと横組みを切り替える。</item>
/// </list>
/// </remarks>
public static class NdlOcrXyCutReadingOrder
{
    /// <summary>文字行の外接矩形。座標はページ画像の画素単位で、右端・下端は開区間として扱う。</summary>
    public readonly record struct LineBox(int X0, int Y0, int X1, int Y1);

    /// <summary>公式版の<c>get_optimal_grid</c>。短辺の分割数を行数から決める。</summary>
    private const double GridBase = 100.0;

    /// <summary>
    /// 各行の読み順位を返す。戻り値の添字は入力と同じ順で、値が小さいほど先に読む。
    /// </summary>
    public static int[] Solve(IReadOnlyList<LineBox> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        if (boxes.Count == 0)
        {
            return [];
        }

        var normalized = Normalize(boxes);
        var table = BuildMeshTable(normalized, out var gridWidth, out var gridHeight);
        var root = new BlockNode(0, 0, gridWidth, gridHeight);
        Cut(table, root);
        AssignBoxesToLeaves(root, normalized);
        SortNodes(root, normalized);

        var ranks = new int[boxes.Count];
        Array.Fill(ranks, -1);
        AssignRanks(root, ranks, 0);
        return ranks;
    }

    /// <summary>公式版の<c>normalize_bboxes</c>（<c>scale</c>=1.0で膨張処理なし）。</summary>
    private static LineBox[] Normalize(IReadOnlyList<LineBox> boxes)
    {
        var normalized = new LineBox[boxes.Count];
        var xMin = int.MaxValue;
        var yMin = int.MaxValue;
        var xMax = int.MinValue;
        var yMax = int.MinValue;

        for (var i = 0; i < boxes.Count; i++)
        {
            // 公式版と同じく、幅・高さが負にならないよう右端・下端を左端・上端まで引き上げる。
            var box = boxes[i] with
            {
                X1 = Math.Max(boxes[i].X0, boxes[i].X1),
                Y1 = Math.Max(boxes[i].Y0, boxes[i].Y1),
            };
            normalized[i] = box;
            xMin = Math.Min(xMin, box.X0);
            yMin = Math.Min(yMin, box.Y0);
            xMax = Math.Max(xMax, box.X1);
            yMax = Math.Max(yMax, box.Y1);
        }

        var pageWidth = xMax - xMin;
        var pageHeight = yMax - yMin;

        var grid = GridBase * Math.Sqrt(boxes.Count);
        // 短辺をgrid分割し、長辺は縦横比の分だけ細かくする。
        // 公式版では辺長0のページで inf になるため、ここでは1点へ潰す。
        var xGrid = pageWidth < pageHeight ? grid : SafeScale(grid, pageWidth, pageHeight);
        var yGrid = pageHeight < pageWidth ? grid : SafeScale(grid, pageHeight, pageWidth);

        for (var i = 0; i < normalized.Length; i++)
        {
            var box = normalized[i];
            normalized[i] = new LineBox(
                ToGrid(box.X0 - xMin, xGrid, pageWidth),
                ToGrid(box.Y0 - yMin, yGrid, pageHeight),
                ToGrid(box.X1 - xMin, xGrid, pageWidth),
                ToGrid(box.Y1 - yMin, yGrid, pageHeight));
        }

        return normalized;
    }

    private static double SafeScale(double grid, long longSide, long shortSide) =>
        shortSide <= 0 ? grid : grid * ((double)longSide / shortSide);

    private static int ToGrid(int offset, double gridSize, int pageSize)
    {
        if (pageSize <= 0)
        {
            return 0;
        }

        var value = Math.Floor(offset * gridSize / pageSize);
        return value <= 0 ? 0 : (int)value;
    }

    /// <summary>公式版の<c>make_mesh_table</c>。行が占める格子を1にする。</summary>
    private static int[,] BuildMeshTable(LineBox[] boxes, out int width, out int height)
    {
        width = boxes.Max(box => box.X1) + 1;
        height = boxes.Max(box => box.Y1) + 1;
        var table = new int[height, width];
        foreach (var box in boxes)
        {
            for (var y = box.Y0; y < box.Y1; y++)
            {
                for (var x = box.X0; x < box.X1; x++)
                {
                    table[y, x] = 1;
                }
            }
        }

        return table;
    }

    /// <summary>公式版の<c>block_xy_cut</c>。切れ目の長さと密度から切断軸を選ぶ。</summary>
    private static void Cut(int[,] table, BlockNode node)
    {
        var integral = IntegralImage.Create(table);
        Cut(integral, node);
    }

    private static void Cut(IntegralImage integral, BlockNode node)
    {
        var (x0, y0, x1, y1) = node.Coordinates;
        var xHistogram = integral.ColumnSums(x0, y0, x1, y1);
        var yHistogram = integral.RowSums(x0, y0, x1, y1);
        var (xBegin, xEnd, xValue) = FindMinimumSpan(xHistogram);
        var (yBegin, yEnd, yValue) = FindMinimumSpan(yHistogram);
        xBegin += x0;
        xEnd += x0;
        yBegin += y0;
        yEnd += y0;

        if (x0 == xBegin && x1 == xEnd && y0 == yBegin && y1 == yEnd)
        {
            return;
        }

        if (yValue < xValue)
        {
            SplitX(integral, node, xBegin, xEnd);
        }
        else if (xValue < yValue)
        {
            SplitY(integral, node, yBegin, yEnd);
        }
        else if (xEnd - xBegin < yEnd - yBegin)
        {
            SplitY(integral, node, yBegin, yEnd);
        }
        else
        {
            SplitX(integral, node, xBegin, xEnd);
        }
    }

    private static void SplitX(IntegralImage integral, BlockNode parent, int xBegin, int xEnd)
    {
        Split(integral, parent, parent.X0, parent.Y0, xBegin, parent.Y1);
        Split(integral, parent, xBegin, parent.Y0, xEnd, parent.Y1);
        Split(integral, parent, xEnd, parent.Y0, parent.X1, parent.Y1);
    }

    private static void SplitY(IntegralImage integral, BlockNode parent, int yBegin, int yEnd)
    {
        Split(integral, parent, parent.X0, parent.Y0, parent.X1, yBegin);
        Split(integral, parent, parent.X0, yBegin, parent.X1, yEnd);
        Split(integral, parent, parent.X0, yEnd, parent.X1, parent.Y1);
    }

    private static void Split(IntegralImage integral, BlockNode parent, int x0, int y0, int x1, int y1)
    {
        if (x0 >= x1 || y0 >= y1)
        {
            return;
        }

        if ((x0, y0, x1, y1) == parent.Coordinates)
        {
            return;
        }

        var child = new BlockNode(x0, y0, x1, y1);
        parent.Children.Add(child);
        Cut(integral, child);
    }

    /// <summary>
    /// 公式版の<c>calc_min_span</c>。最小値が最も長く連続する区間と、その密度<c>-min/max</c>を返す。
    /// </summary>
    private static (int Begin, int End, double Value) FindMinimumSpan(int[] histogram)
    {
        if (histogram.Length == 1)
        {
            // 公式版は長さ1のヒストグラムをそのまま返す。ここでは唯一の要素を密度として扱う。
            return (0, 1, histogram[0]);
        }

        var minimum = histogram.Min();
        var maximum = histogram.Max();

        var bestBegin = 0;
        var bestEnd = 0;
        var bestLength = -1;
        var runBegin = -1;
        for (var i = 0; i <= histogram.Length; i++)
        {
            var isMinimum = i < histogram.Length && histogram[i] == minimum;
            if (isMinimum && runBegin < 0)
            {
                runBegin = i;
            }
            else if (!isMinimum && runBegin >= 0)
            {
                if (i - runBegin > bestLength)
                {
                    bestBegin = runBegin;
                    bestEnd = i;
                    bestLength = i - runBegin;
                }

                runBegin = -1;
            }
        }

        var value = maximum > 0 ? -(double)minimum / maximum : 0.0;
        return (bestBegin, bestEnd, value);
    }

    /// <summary>公式版の<c>assign_bbox_to_node</c>。重なりが最大の葉へ各行を割り当てる。</summary>
    private static void AssignBoxesToLeaves(BlockNode root, LineBox[] boxes)
    {
        var leaves = new List<BlockNode>();
        CollectLeaves(root, leaves);
        if (leaves.Count == 0)
        {
            return;
        }

        for (var i = 0; i < boxes.Length; i++)
        {
            // 同点なら先に見つかった葉（深さ優先で前にある葉）を選ぶ。公式版のnanargmaxと同じ。
            var best = 0;
            var bestScore = Overlap(boxes[i], leaves[0]);
            for (var j = 1; j < leaves.Count; j++)
            {
                var score = Overlap(boxes[i], leaves[j]);
                if (score > bestScore)
                {
                    best = j;
                    bestScore = score;
                }
            }

            leaves[best].LineIndices.Add(i);
        }
    }

    private static void CollectLeaves(BlockNode node, List<BlockNode> leaves)
    {
        if (node.Children.Count == 0)
        {
            leaves.Add(node);
            return;
        }

        foreach (var child in node.Children)
        {
            CollectLeaves(child, leaves);
        }
    }

    /// <summary>
    /// 行が葉に含まれる割合（交差面積 / 行の面積）。
    /// </summary>
    /// <remarks>
    /// 公式版の<c>calc_iou</c>は、面積を<c>box[2] - boxes[:,0] + 1</c>のように行と葉の座標を
    /// 取り違えて求めており、IoUにもなっていない。行がどの葉に属するかを決める量としては
    /// 行の面積に対する交差の割合が適切なため、この式に改めた。格子化で幅または高さが0になった
    /// 行も扱えるよう、公式版と同じく両端を含む（+1）画素数で数える。
    /// 戻り値は0～1で、NaNにはならない。
    /// </remarks>
    public static double Overlap(LineBox box, int nodeX0, int nodeY0, int nodeX1, int nodeY1)
    {
        var intersectionWidth = Math.Max(0, Math.Min(box.X1, nodeX1) - Math.Max(box.X0, nodeX0) + 1);
        var intersectionHeight = Math.Max(0, Math.Min(box.Y1, nodeY1) - Math.Max(box.Y0, nodeY0) + 1);
        double intersection = (long)intersectionWidth * intersectionHeight;
        double boxArea = (long)(box.X1 - box.X0 + 1) * (box.Y1 - box.Y0 + 1);
        return boxArea <= 0 ? 0 : Math.Min(1.0, intersection / boxArea);
    }

    private static double Overlap(LineBox box, BlockNode node) =>
        Overlap(box, node.X0, node.Y0, node.X1, node.Y1);

    /// <summary>公式版の<c>sort_nodes</c>。葉の中で行を並べ、縦組みの左右分割では子を反転する。</summary>
    private static (int Lines, int VerticalLines) SortNodes(BlockNode node, LineBox[] boxes)
    {
        if (node.LineIndices.Count > 0)
        {
            node.LineCount = node.LineIndices.Count;
            node.VerticalLineCount = node.LineIndices.Count(index =>
                boxes[index].X1 - boxes[index].X0 < boxes[index].Y1 - boxes[index].Y0);

            if (node.LineCount > 1)
            {
                var sorted = node.IsVertical
                    // 縦組みは右の行から左へ、同じ位置なら上から下へ。
                    ? node.LineIndices
                        .OrderByDescending(index => boxes[index].X0)
                        .ThenBy(index => boxes[index].Y0)
                        .ToList()
                    // 横組みは上の行から下へ、同じ位置なら左から右へ。
                    : node.LineIndices
                        .OrderBy(index => boxes[index].Y0)
                        .ThenBy(index => boxes[index].X0)
                        .ToList();
                node.LineIndices.Clear();
                node.LineIndices.AddRange(sorted);
            }

            return (node.LineCount, node.VerticalLineCount);
        }

        foreach (var child in node.Children)
        {
            var (lines, verticalLines) = SortNodes(child, boxes);
            node.LineCount += lines;
            node.VerticalLineCount += verticalLines;
        }

        if (node.IsHorizontalSplit && node.IsVertical)
        {
            node.Children.Reverse();
        }

        return (node.LineCount, node.VerticalLineCount);
    }

    /// <summary>公式版の<c>get_ranking</c>。深さ優先で通し番号を振る。</summary>
    private static int AssignRanks(BlockNode node, int[] ranks, int rank)
    {
        foreach (var index in node.LineIndices)
        {
            ranks[index] = rank;
            rank++;
        }

        foreach (var child in node.Children)
        {
            rank = AssignRanks(child, ranks, rank);
        }

        return rank;
    }

    private sealed class BlockNode(int x0, int y0, int x1, int y1)
    {
        public int X0 { get; } = x0;

        public int Y0 { get; } = y0;

        public int X1 { get; } = x1;

        public int Y1 { get; } = y1;

        public List<BlockNode> Children { get; } = [];

        public List<int> LineIndices { get; } = [];

        public int LineCount { get; set; }

        public int VerticalLineCount { get; set; }

        public (int X0, int Y0, int X1, int Y1) Coordinates => (X0, Y0, X1, Y1);

        /// <summary>公式版の<c>is_vertical</c>。行の過半が縦長ならその節は縦組みとみなす。</summary>
        public bool IsVertical => LineCount < VerticalLineCount * 2;

        /// <summary>公式版の<c>is_x_split</c>。すべての子が親と同じY範囲なら左右分割である。</summary>
        public bool IsHorizontalSplit => Children.All(child => child.Y0 == Y0 && child.Y1 == Y1);
    }

    /// <summary>格子表の積分画像。各切断で行和・列和を面積に依存せず求めるために使う。</summary>
    private sealed class IntegralImage
    {
        private readonly int[,] sums;

        private IntegralImage(int[,] sums) => this.sums = sums;

        public static IntegralImage Create(int[,] table)
        {
            var height = table.GetLength(0);
            var width = table.GetLength(1);
            var sums = new int[height + 1, width + 1];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    sums[y + 1, x + 1] = table[y, x]
                        + sums[y, x + 1]
                        + sums[y + 1, x]
                        - sums[y, x];
                }
            }

            return new IntegralImage(sums);
        }

        /// <summary>x0以上x1未満の各列について、y0以上y1未満の行の合計を返す。</summary>
        public int[] ColumnSums(int x0, int y0, int x1, int y1)
        {
            var result = new int[x1 - x0];
            for (var i = 0; i < result.Length; i++)
            {
                var x = x0 + i;
                result[i] = sums[y1, x + 1] - sums[y0, x + 1] - sums[y1, x] + sums[y0, x];
            }

            return result;
        }

        /// <summary>y0以上y1未満の各行について、x0以上x1未満の列の合計を返す。</summary>
        public int[] RowSums(int x0, int y0, int x1, int y1)
        {
            var result = new int[y1 - y0];
            for (var j = 0; j < result.Length; j++)
            {
                var y = y0 + j;
                result[j] = sums[y + 1, x1] - sums[y + 1, x0] - sums[y, x1] + sums[y, x0];
            }

            return result;
        }
    }
}
