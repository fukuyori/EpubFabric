namespace EpubFabric.Core.Models;

/// <summary>
/// ブロック種別の表示色（#RRGGBB）。評価レポートの凡例と
/// ページ画像へのオーバーレイ描画で同じ色を使うため、ここで一元管理する。
/// </summary>
public static class BlockTypeColors
{
    public static readonly IReadOnlyDictionary<BlockType, string> Hex = new Dictionary<BlockType, string>
    {
        [BlockType.ChapterTitle] = "#E53935",
        [BlockType.SectionHeading] = "#FB8C00",
        [BlockType.Subheading] = "#FDD835",
        [BlockType.Body] = "#1E88E5",
        [BlockType.Figure] = "#43A047",
        [BlockType.Caption] = "#00ACC1",
        [BlockType.Aside] = "#8E24AA",
        [BlockType.PullQuote] = "#D81B60",
        [BlockType.Table] = "#6D4C41",
        [BlockType.Footnote] = "#3949AB",
        [BlockType.Code] = "#00897B",
        [BlockType.Header] = "#9E9E9E",
        [BlockType.Footer] = "#9E9E9E",
        [BlockType.PageNumber] = "#9E9E9E",
        [BlockType.Decorative] = "#BDBDBD",
        [BlockType.Unknown] = "#757575",
    };

    public static string HexFor(BlockType type) => Hex.GetValueOrDefault(type, "#757575");
}
