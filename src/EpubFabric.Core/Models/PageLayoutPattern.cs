namespace EpubFabric.Core.Models;

/// <summary>
/// ページ本文の主要な書字方向と段組み。表紙・広告・キャプションなどの混在要素は
/// 主要パターンには含めず、個別ブロックとして扱う。
/// </summary>
public enum PageLayoutPattern
{
    HorizontalSingleColumn,
    HorizontalTwoColumn,
    VerticalSingleColumn,
    VerticalTwoColumn,
    HorizontalComplex,
}
