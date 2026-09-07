# Changelog

[日本語](CHANGELOG.ja.md)

The format of this file is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.4.0] - 2026-09-07

### Added

- Classify each page as horizontal single-column, horizontal two-column, vertical single-column, vertical two-column, or complex horizontal layout, and apply layout-specific reflow ordering
- Structure reflow output as articles with titles, kickers, authors, affiliations, abstracts, headings, body text, figures, captions, footnotes, and references
- Add separate Japanese README and changelog documents while keeping the root documentation in English

### Changed

- Improve reflow extraction from complex PDFs by reconciling fragmented PDF text lines, merging paragraphs across page boundaries, filtering source table-of-contents blocks, and tightening structural heading detection
- Improve preservation of code blocks, figures, captions, and article boundaries in generated EPUB content

## [0.3.0] - 2026-09-06

### Added

- Detect repeating running headers, footers, and page numbers across all pages while restoring non-repeating margin text to the body
- Added footnote detection based on small text near the bottom of a page and footnote markers
- Added content-based heuristics for numbered English headings and common book-heading patterns
- Renamed the distribution script from `publish.ps1` to `build-installer.ps1`, and added `-Sign` support for digitally signing the CLI, GUI, installer, and uninstaller

## [0.2.4] - 2026-07-29

### Fixed

- Moved each file's processing status from the right edge to the left edge of the PDF list in the GUI
- Added confirmation when closing the GUI during PDF conversion, allowing the user to cancel the conversion and exit

## [0.2.3] - 2026-07-29

### Added

- Added sequential conversion of multiple PDFs
  - The **GUI** was redesigned around a list of PDFs to convert. Each entry shows the file name, folder, and status: waiting, converting, completed, failed, or canceled. Because drag and drop adds files to the list, files can be dropped one at a time to build the list incrementally. The list can be edited with “Add...,” “Remove selected,” and “Remove all.” Output is now configured solely through “Output folder”; when omitted, `input-name.epub` is written next to each PDF
  - The **CLI** now accepts commands in the form `convert a.pdf b.pdf c.pdf` and treats `--output` as an output directory
  - In both interfaces, the remaining conversions continue if one file fails, and the final result reports the numbers of successful and failed conversions
- Added the version next to the GUI screen title, for example `v0.2.3`

### Fixed

- Fixed the GUI processing log not following new entries automatically and showing only the beginning. Because ListView does not materialize rows outside the visible range, requests to scroll to the end immediately after adding a row had no effect. The inner scroll position is now changed directly after layout completes. A 5,000-line limit was also added so the log cannot grow indefinitely during sequential conversion
- Fixed the GUI options being arranged in one horizontal row, which caused options on the right, such as the Ollama model, to disappear beyond the window width. Value inputs and checkboxes are now split across two rows, and horizontal scrolling remains available when the window is narrowed further

## [0.2.2] - 2026-07-29

### Changed

- Renamed the executables to reflect the GUI's role as the main application. The GUI changed from `EpubFabric.App.exe` to **`EpubFabric.exe`**, and the window title changed to “EpubFabric.” The CLI changed from `epubfabric.exe` to **`epubfabric-cli.exe`**
- Reorganized the installer layout: the GUI is placed at the application root, which is added to PATH, and the CLI is placed under `cli\` as a supporting tool. **Entering `epubfabric` now launches the GUI**; previously it launched the CLI
- Changed the defaults of `build.ps1` to Release / win-x64. These now match the defaults of `publish.ps1`, so running both scripts in sequence without arguments produces an installer. Previously, the build defaulted to Debug and could not be followed by publish. Use `-Configuration Debug` for a debug build and `-Runtime ""` to skip the distribution build
- Changed `publish.ps1` to create an installer by default. The `-Installer` switch was removed; use `-SkipInstaller` to create only the distribution folders. When `-SkipGui` is specified, the installer is skipped automatically because it cannot include the GUI

### Fixed

- Fixed body text in reflowable EPUBs being split in the middle of paragraphs on pages with a vertical sidebar, such as an article title, at the page edge. The sidebar was not recognized as an independent column and was inserted between body lines. The gutter search area now extends closer to the page edge, the minimum proportion for each column was relaxed, and **cross-column detection was changed from asking whether an item is wide to checking whether it actually crosses a gutter**. Previously, body lines on single-column pages were judged to be wide and split into individual lines. In the first 40 pages of Kagaku 202601, average characters per block improved from 60.5 to 61.5 with no regressions
- Fixed paragraph drop caps being separated into independent blocks, which split text such as “2” and “025年2月…”. A drop cap is now joined to the start of the following line
- Fixed large deskew corrections being applied to photograph-dominant pages with no body-text lines, even though they were not tilted. Subject outlines produced peaks in the projection profile; actual data yielded -10.5° for a back cover and 6.2° for a frontispiece. The implementation now checks whether the projection score improved enough over the unrotated image to indicate aligned lines, and skips correction otherwise. Measurements separated false estimates, with improvement ratios of 1.05 or less, from actual skew, with ratios of 1.8 or more. Also fixed the fine search returning values beyond the search limit, such as ±10.5°
- Changed `--enhance` correction values from per-page estimates to the **paper color of the entire book**. Paper color is measured for every page, pages with unusable measurements such as frontispieces and full-page photographs are removed as outliers using median absolute deviation (MAD), and the median is used. Per-page estimates varied with factors such as photograph area, producing inconsistent brightness across adjacent pages; measurements for Chiri 202601 ranged from 246 to 255 in estimated paper luminance. When fewer than three pages can be identified as paper pages, the previous per-page estimation remains in use. The decision whether to modify a page is still made per page, so photograph pages remain untouched as before
- Fixed pale illustrations and colored backgrounds sometimes fading to white because `--enhance` whitening considered only brightness. Whitening now targets only pixels with low saturation and a hue close to the paper color. Because the comparison uses differences between channels rather than simply how much darker a pixel is than the paper, bleed-through and uneven backgrounds continue to be whitened as before
- Reduced the GUI's overly large initial window size. It now opens centered at 1180×820, scaled for the display's DPI
- Fixed the “Use first page as cover image (reflow)” option remaining enabled during GUI conversion; it is now disabled like the other options
- Fixed Ollama classification and correction failing in GUI distribution builds with “Reflection-based serialization has been disabled for this application.” Enabling trimming made the SDK write `System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault=false` into runtimeconfig, causing `OllamaClient`, which uses reflection-based JSON serialization, to throw at runtime. Because this setting is determined at build time and cannot be overridden during publish, trimming was disabled in the project

## [0.2.1] - 2026-07-29

### Changed

- Reorganized the build and distribution scripts so that **only `scripts\build.ps1` compiles the application**
  - Added `scripts\build.ps1`, which builds and tests the solution; `-Runtime` creates a self-contained distribution build, and `-SkipTests`, `-TestFilter`, and `-Clean` are supported
  - Changed `scripts\publish.ps1` to use `dotnet publish --no-build`. It no longer compiles or tests and only turns built binaries into a distribution. If no distribution build exists, it stops and displays the command that must be run first
  - Integrated `scripts\build-installer.ps1` into `publish.ps1` and removed it

## [0.2.0] - 2026-07-29

### Added

- Created an application icon featuring a page and bookmark ribbon. It replaces the default WinUI template placeholder, includes nine ICO sizes from 16 to 256 pixels, and is embedded in the CLI and GUI executables and the installer
- Added `--cover-image` for reflowable EPUBs, with a corresponding GUI checkbox. It stores the first page as an image without converting it to text. Because covers often contain decorative text that OCR misrecognizes, this option preserves the original page without introducing those errors into the body
- Added the GUI (`EpubFabric.App`) to the installer and provided Start menu and desktop shortcuts

### Fixed

- Fixed the reading order in fixed-layout EPUBs alternating one line at a time between the left and right columns on two-column pages. After column splitting, lines within a column were incorrectly classified as wide items crossing columns and separated into one-line groups. The number of pages with broken reading order improved from 47 to 3 out of 102 pages in Kagaku 202601, and from 9 to 2 in the first 40 pages of Chiri 202601
- Fixed figures in reflowable EPUBs extending beyond the screen width. The stylesheet lacked an image size limit (`max-width: 100%`), so extracted figures were displayed at their original resolution
- Fixed fixed-layout EPUBs using the original image instead of the enhanced `--enhance` image when recompression was unnecessary

## [0.1.4] - 2026-07-18

### Added

- Added automatic detection of publication language (`dc:language`) from recognized-text character ratios for Japanese, English, Chinese, and Korean (`ja`, `en`, `zh`, and `ko`). The language can be forced with `--language`
- Added `--force-ocr` to ignore the PDF text layer and rerun OCR on every page, intended for PDFs with low-quality text layers from older scan OCR
- Added `--max-pages` to stop conversion after the first n pages for trial conversions and configuration tuning

### Fixed

- Made the table of contents consistent with body headings. The table of contents is now generated from the same data as body headings, section headings are nested under chapters, and chapter-title eligibility rules suppress decorative text and fragments from appearing as chapters

## [0.1.3] - 2026-07-18

### Fixed

- Prevented long sequences of digits caused by halftone patterns from entering body text. Low-confidence sequences of 16 or more digits without separators are discarded; separators exempt values such as ISBNs and telephone numbers
- Reduced EPUB size by extracting figures in reflowable EPUBs as JPEG images

## [0.1.2] - 2026-07-18

### Added

- Added bold-heading detection. It measures each line's black-pixel ratio (ink density) and treats lines darker than body text as headings, allowing same-size sans-serif headings to be detected

## [0.1.1] - 2026-07-18

### Fixed

- Improved usability of the GUI's three-pane proofreading screen

## [0.1.0] - 2026-07-18

Initial release, providing a complete workflow for converting PDFs—both scanned documents and PDFs with text layers—to EPUB 3.

### Added

- Fixed-layout EPUB generation with page images and positioned transparent text layers; the first page is designated as the cover
- Reflowable EPUB generation with chapter structure created through layout analysis and paragraph merging
- OCR using RapidOcrNet and the multilingual PP-OCRv6 ONNX model, with Japanese support, deskewing, and low-confidence noise-line filtering
- Vertical writing support with per-page writing-direction detection, right binding, right-to-left reading order, and vertical text layers
- Multi-column reading-order detection supporting two to four columns and uneven widths through recursive gutter detection
- Page enhancement with `--enhance`, including paper-color white-balance normalization and suppression of bleed-through and uneven backgrounds
- Ollama integration with `--ollama` for semantic correction of block types and heading levels and correction of OCR errors, protected by multiple safeguards such as equal-length replacement only
- Size optimization through page-image recompression with `--image-quality` and `--max-image-size`
- Conversion-accuracy reports with `evaluate`, providing side-by-side HTML views of page images and EPUB fragments plus metrics
- Analysis, manual proofreading, and export workflow using `.efproj` project files with `analyze` and `export`
- Windows GUI (WinUI 3) with conversion and three-pane proofreading screens
- Distribution scripts and an Inno Setup installer

[Unreleased]: https://github.com/fukuyori/EpubFabric/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/fukuyori/EpubFabric/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/fukuyori/EpubFabric/compare/v0.2.4...v0.3.0
[0.2.4]: https://github.com/fukuyori/EpubFabric/compare/v0.2.3...v0.2.4
[0.2.3]: https://github.com/fukuyori/EpubFabric/compare/v0.2.2...v0.2.3
[0.2.2]: https://github.com/fukuyori/EpubFabric/compare/v0.2.1...v0.2.2
[0.2.1]: https://github.com/fukuyori/EpubFabric/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/fukuyori/EpubFabric/compare/v0.1.4...v0.2.0
[0.1.4]: https://github.com/fukuyori/EpubFabric/compare/v0.1.3...v0.1.4
[0.1.3]: https://github.com/fukuyori/EpubFabric/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/fukuyori/EpubFabric/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/fukuyori/EpubFabric/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/fukuyori/EpubFabric/releases/tag/v0.1.0
