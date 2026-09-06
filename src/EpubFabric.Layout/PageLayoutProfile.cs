using EpubFabric.Core.Models;

namespace EpubFabric.Layout;

/// <summary>ページの主要レイアウト分類と、2段組みの場合の読み座標上の段間位置。</summary>
public sealed record PageLayoutProfile(
    PageLayoutPattern Pattern,
    double Confidence,
    double? GutterPosition = null)
{
    public WritingMode WritingMode => Pattern is
        PageLayoutPattern.VerticalSingleColumn or PageLayoutPattern.VerticalTwoColumn
            ? WritingMode.Vertical
            : WritingMode.Horizontal;

    public int ColumnCount => Pattern is
        PageLayoutPattern.HorizontalTwoColumn or PageLayoutPattern.VerticalTwoColumn ? 2 : 1;

    public bool UsesGenericColumnDetection => Pattern is
        PageLayoutPattern.HorizontalComplex;
}
