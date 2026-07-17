namespace EpubFabric.Core.Models;

/// <summary>
/// 座標はページサイズに対する0～1の比率で保持する。
/// </summary>
public readonly record struct BoundingBox(
    double X,
    double Y,
    double Width,
    double Height);
