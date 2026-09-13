# Third-party notices

## NDLOCR-Lite-derived ONNX integration

Portions of the following files adapt work from
[ndl-lab/ndlocr-lite](https://github.com/ndl-lab/ndlocr-lite), commit
`d25e0d415b607ad44459ca6b95c7512a54363935`:

- `src/EpubFabric.Pipeline/NativeNdlOcrService.cs` — model preprocessing,
  postprocessing, and the recognition cascade
- `src/EpubFabric.Pipeline/NdlOcrXyCutReadingOrder.cs` — the recursive XY-cut
  reading order of `reading_order/xy_cut/block_xy_cut.py`

Copyright (c) 2023 National Diet Library, Japan.

The upstream work is licensed under the
[Creative Commons Attribution 4.0 International License](https://creativecommons.org/licenses/by/4.0/).
The C# adaptation changes the runtime integration, image resizing, duplicate
suppression, and error handling. In the reading order it keeps the upstream
algorithm and replaces the NumPy array operations with scalar loops and an
integral image, and it resolves the degenerate cases that NumPy would produce
as infinity or NaN.

NDLOCR-Lite model files are not included in this repository or its current
distribution. Before distributing the models, their attribution and the
licenses and notices of their dependencies must also be included.
