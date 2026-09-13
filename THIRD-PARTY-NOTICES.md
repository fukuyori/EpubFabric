# Third-party notices

## NDLOCR-Lite-derived ONNX integration

Portions of `src/EpubFabric.Pipeline/NativeNdlOcrService.cs` adapt the model
preprocessing, postprocessing, and recognition cascade from
[ndl-lab/ndlocr-lite](https://github.com/ndl-lab/ndlocr-lite), commit
`d25e0d415b607ad44459ca6b95c7512a54363935`.

Copyright (c) 2023 National Diet Library, Japan.

The upstream work is licensed under the
[Creative Commons Attribution 4.0 International License](https://creativecommons.org/licenses/by/4.0/).
The C# adaptation changes the runtime integration, image resizing, duplicate
suppression, error handling, and reading-order implementation.

NDLOCR-Lite model files are not included in this repository or its current
distribution. Before distributing the models, their attribution and the
licenses and notices of their dependencies must also be included.
