namespace EpubFabric.Core.Models;

public enum NonTextRegionKind
{
    /// <summary>OCR文字が乗っていない、絵柄（写真・イラスト・グラフ）を含む領域。</summary>
    Figure,

    /// <summary>矩形の罫線で囲まれた領域。囲み記事の候補。</summary>
    Boxed,
}

/// <summary>
/// 9.4 レイアウト解析：OCRでは検出できない非テキスト領域（図・囲み記事の罫線）の候補。
/// </summary>
public sealed record NonTextRegion(BoundingBox Bounds, NonTextRegionKind Kind);
