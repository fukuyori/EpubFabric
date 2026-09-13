# Third-party notices

EpubFabric is licensed under the Apache License 2.0 (see `LICENSE` and
`NOTICE`). That license covers EpubFabric's own code only. The components
listed here keep their own terms.

## Redistributed components

An EpubFabric distribution is self-contained, so it carries the binaries below
in addition to EpubFabric's own assemblies. Each entry names the license of the
package as published on NuGet; where a package carries native code from another
project, that project and its own license are named as well.

### Managed assemblies

| Component | License | Project |
|---|---|---|
| Clipper2Lib | Boost Software License 1.0 | <https://www.angusj.com/clipper2/> |
| Docnet.Core | MIT | <https://github.com/GowenGit/docnet> |
| Microsoft.Data.Sqlite | MIT | <https://github.com/dotnet/efcore> |
| Microsoft.Extensions.\* (Configuration, DependencyInjection, Logging, Options, Primitives) | MIT | <https://github.com/dotnet/runtime> |
| Microsoft.ML.OnnxRuntime | MIT | <https://github.com/microsoft/onnxruntime> |
| OpenCvSharp | Apache License 2.0 | <https://github.com/shimat/opencvsharp> |
| RapidOcrNet | Apache License 2.0 | <https://github.com/BobLd/RapidOcrNet> |
| SkiaSharp | MIT | <https://github.com/mono/SkiaSharp> |
| SQLitePCLRaw (core, batteries_v2, provider.e_sqlite3) | Apache License 2.0 | <https://github.com/ericsink/SQLitePCL.raw> |
| System.Numerics.Tensors | MIT | <https://github.com/dotnet/runtime> |

### Native libraries

| File | Shipped by | Upstream project |
|---|---|---|
| `pdfium.dll` | Docnet.Core (MIT) | PDFium — Copyright 2014 The PDFium Authors, BSD 3-clause. The full text ships in the Docnet.Core package as `runtimes/<rid>/native/LICENSE` |
| `OpenCvSharpExtern.dll` | OpenCvSharp4.runtime.win (Apache License 2.0) | OpenCV — <https://github.com/opencv/opencv> |
| `onnxruntime.dll`, `onnxruntime_providers_shared.dll` | Microsoft.ML.OnnxRuntime (MIT) | ONNX Runtime. The package carries `ThirdPartyNotices.txt` for the material it incorporates |
| `libSkiaSharp.dll` | SkiaSharp.NativeAssets.Win32 (MIT, Copyright (c) 2015-2016 Xamarin, Inc. and 2017-2018 Microsoft Corporation) | Skia and related projects. The package carries `THIRD-PARTY-NOTICES.txt` |
| `e_sqlite3.dll` | SQLitePCLRaw.lib.e_sqlite3 (Apache License 2.0) | SQLite — <https://sqlite.org/> |

`opencv_videoio_ffmpeg*.dll` ships inside OpenCvSharp4.runtime.win but is
**excluded from every EpubFabric build and distribution** by
`Directory.Build.targets`. EpubFabric never uses OpenCV video input or output,
and that library embeds FFmpeg, which carries obligations EpubFabric has no
reason to take on.

### OCR models

`src/EpubFabric.Ocr/Models/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx` is
checked into this repository and embedded in `EpubFabric.Ocr.dll`, so it is
redistributed with EpubFabric. It is the text-line orientation classifier that
ships in the RapidOcrNet package (Apache License 2.0) and originates from
PaddleOCR — <https://github.com/PaddlePaddle/PaddleOCR>.

The PP-OCRv6 detection and recognition models and their dictionary are **not**
redistributed. EpubFabric downloads them on first use from the RapidOCR model
listing on ModelScope into `%LOCALAPPDATA%\EpubFabric\models\ppocrv6-small`,
verifying the published SHA-256 for the two model files.

NDLOCR-Lite models are not redistributed either; see below.

## NDLOCR-Lite-derived ONNX integration

Portions of the following files adapt work from
[ndl-lab/ndlocr-lite](https://github.com/ndl-lab/ndlocr-lite), commit
`d25e0d415b607ad44459ca6b95c7512a54363935`:

- `src/EpubFabric.Pipeline/NativeNdlOcrService.cs` — model preprocessing,
  postprocessing, and the recognition cascade
- `src/EpubFabric.Pipeline/NdlOcrXyCutReadingOrder.cs` — the recursive XY-cut
  reading order of `reading_order/xy_cut/block_xy_cut.py`
- `src/EpubFabric.Pipeline/NdlOcrAdjacentDuplicateFilter.cs` — the adjacent
  duplicate removal of `reading_order/order/reorder.py`

Copyright (c) 2023 National Diet Library, Japan.

The upstream work is licensed under the
[Creative Commons Attribution 4.0 International License](https://creativecommons.org/licenses/by/4.0/).
The C# adaptation changes the runtime integration, image resizing, duplicate
suppression, and error handling. In the reading order it keeps the upstream
algorithm and replaces the NumPy array operations with scalar loops and an
integral image, and it resolves the degenerate cases that NumPy would produce
as infinity or NaN. Upstream removes adjacent duplicates inside each TEXTBLOCK
and among the children of PAGE; because EpubFabric keeps one flat list of lines
per page, the adaptation applies the same rule across the page in reading order.

NDLOCR-Lite model files are not included in this repository or in any
EpubFabric distribution, and bundling them is not planned. Users download them
themselves and pass the directory with `--ndlocr-model-dir`; EpubFabric falls
back to RapidOCR when the models are absent. See the README for the steps.

EpubFabric itself is licensed under the Apache License 2.0. That license
applies to EpubFabric's own code and does not extend to NDLOCR-Lite or to any
other third-party work listed here, which stay under their own terms.
