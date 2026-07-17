namespace EpubFabric.Core.Models;

/// <summary>
/// OCRまたはテキストレイヤー抽出で得られる、分類前の行単位の認識結果。
/// レイアウト解析（見出し検出・段組み判定）の入力として使う。
/// </summary>
public sealed record TextLine(
    BoundingBox Bounds,
    string Text,
    double Confidence,
    TextSourceKind Source = TextSourceKind.Unknown);
