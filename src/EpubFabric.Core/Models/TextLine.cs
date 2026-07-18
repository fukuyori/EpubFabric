namespace EpubFabric.Core.Models;

/// <summary>
/// OCRまたはテキストレイヤー抽出で得られる、分類前の行単位の認識結果。
/// レイアウト解析（見出し検出・段組み判定）の入力として使う。
/// InkDensityは行ボックス内の黒画素率（0〜1）。太字は同じ文字サイズでも
/// 画数あたりのインクが多いため、高さ基準では検出できない太字見出しの
/// 手がかりになる。未測定ならnull。
/// </summary>
public sealed record TextLine(
    BoundingBox Bounds,
    string Text,
    double Confidence,
    TextSourceKind Source = TextSourceKind.Unknown,
    double? InkDensity = null);
