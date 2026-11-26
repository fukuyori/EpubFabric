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

NDLOCR-Lite models are redistributed with the installer; see below.

## NDLOCR-Lite-derived ONNX integration

Portions of the following files adapt work from
[ndl-lab/ndlocr-lite](https://github.com/ndl-lab/ndlocr-lite) v2.0.0, commit
`ba8bb85` (first ported from commit
`d25e0d415b607ad44459ca6b95c7512a54363935`):

- `src/EpubFabric.Pipeline/NativeNdlOcrService.cs` — model preprocessing,
  postprocessing, overlapping-region suppression, and the recognition cascade
- `src/EpubFabric.Pipeline/NdlOcrXyCutReadingOrder.cs` — the recursive XY-cut
  reading order of `reading_order/xy_cut/block_xy_cut.py`
- `src/EpubFabric.Pipeline/NdlOcrAdjacentDuplicateFilter.cs` — the adjacent
  duplicate removal of `reading_order/order/reorder.py`

Copyright (c) 2023 National Diet Library, Japan.

The upstream work is licensed under the
[Creative Commons Attribution 4.0 International License](https://creativecommons.org/licenses/by/4.0/).
The C# adaptation changes the runtime integration, image resizing, and error
handling, and it passes the detected figure, table, running-head, and folio
regions to EpubFabric's layout analysis. In the reading order it keeps the
upstream algorithm and replaces the NumPy array operations with scalar loops
and an integral image, and it resolves the degenerate cases that NumPy would
produce as infinity or NaN. It also changes two upstream behaviors: it assigns
each line to the XY-cut leaf that contains the largest share of the line
(upstream `calc_iou` mixes the line and leaf coordinates), and it sorts the
lines inside a leaf top to bottom when most of them are horizontal (upstream
evaluates `node.is_vertical` without calling it, so leaves are always sorted
right to left). Upstream removes adjacent duplicates inside each TEXTBLOCK
and among the children of PAGE; because EpubFabric keeps one flat list of lines
per page, the adaptation applies the same rule across the page in reading order.

### Bundled NDLOCR-Lite models

The EpubFabric installer places the following unmodified files from
ndl-lab/ndlocr-lite v2.0.0 (`src/model` and `src/config`) in
`{app}\models\ndlocr`:

- `deim-s-1024x1024-261124.onnx` — layout detection (DEIMv2)
- `parseq-ndl-24x256-30-tiny-499epoch-261124.onnx`,
  `parseq-ndl-24x384-50-tiny-278epoch-261124.onnx`,
  `parseq-ndl-24x768-100-tiny-153epoch-261124.onnx` — text recognition (PARSeq)
- `NDLmoji.yaml` — the recognition character set

Copyright (c) 2023 National Diet Library, Japan, licensed under CC BY 4.0.
They are not stored in this repository; `scripts/build-installer.ps1
-NdlOcrModelSource` copies them from an ndlocr-lite checkout.

**DINOv3 License.** The backbone of the layout detection model distills
Meta's DINOv3 into a ViT-Tiny model. The NDLOCR-Lite README asks users of the
bundled trained models to also comply with the DINOv3 License, and section
1.b.i of that license requires a copy of the agreement to accompany any
distribution. The full text is in
[`licenses/ndlocr-lite/LICENCE_DEPENDENCEIES`](licenses/ndlocr-lite/LICENCE_DEPENDENCEIES)
under "Weights of DEIMv2 / DINOv3 License", and the installer copies it,
together with the NDLOCR-Lite `LICENCE`, into `{app}\models\ndlocr`. Among
other terms, the DINOv3 License prohibits uses subject to ITAR and military,
nuclear, espionage, and certain weapons uses (section 1.b.v), and Meta may
amend it (section 8). By using the bundled layout model you agree to those
terms.

When the models are absent (for example in a development build), EpubFabric
reads every page with RapidOCR. `--ndlocr-model-dir` selects another model
directory.

EpubFabric itself is licensed under the Apache License 2.0. That license
applies to EpubFabric's own code and does not extend to NDLOCR-Lite or to any
other third-party work listed here, which stay under their own terms.
