using EpubFabric.Core.Models;
using EpubFabric.Document;
using EpubFabric.Epub;
using EpubFabric.Imaging;
using EpubFabric.Layout;
using EpubFabric.Ocr;
using EpubFabric.Ollama;
using EpubFabric.Pdf;

namespace EpubFabric.Pipeline;

/// <summary>
/// PDF→EPUB変換パイプラインのオーケストレーション。ページのラスタライズ・高品質化・
/// テキスト層評価・OCR（前処理/ゴミ行除去込み）・レイアウト解析・Ollama補正を統合し、
/// CLI（Program.cs）とGUI（EpubFabric.App）の両方から同じ処理を使えるようにする。
/// 進捗はIProgressで通知し、CancellationTokenでページ境界の中断に対応する。
/// </summary>
public sealed class ConversionPipeline
{
    public async Task<(EpubFabricProject Project, List<DocumentPage> Pages)> BuildProjectAsync(
        ConversionOptions options,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var workDirectory = options.WorkDirectory
            ?? Path.Combine(Path.GetTempPath(), $"epubfabric-{Guid.NewGuid():N}");

        var pdfService = new PdfDocumentService();
        var textLayerEvaluator = new PdfTextLayerQualityEvaluator();
        var layoutAnalyzer = new HeuristicLayoutAnalyzer();
        var paragraphMerger = new ParagraphMerger();
        var textLayerBlockBuilder = new TextLayerBlockBuilder();
        var regionDetector = new NonTextRegionDetector();
        var figureExtractor = new FigureImageExtractor();
        var ocrPreprocessor = new OcrImagePreprocessor();
        var inkDensityMeasurer = new LineInkDensityMeasurer();
        var pageEnhancer = options.EnhancePages ? new PageImageEnhancer() : null;
        var ndlOcrService = options.NdlOcr is { } ndlOcrOptions
            ? new NdlOcrService(ndlOcrOptions)
            : null;
        var ppDocLayoutService = options.PpDocLayout is { } ppDocLayoutOptions
            ? new PpDocLayoutService(ppDocLayoutOptions)
            : null;
        var info = pdfService.GetInfo(options.InputPath);

        // 先頭からの変換ページ数（--max-pages。試し変換・設定調整用）。
        var pageCount = options.MaxPages is { } maxPages && maxPages > 0
            ? Math.Min(maxPages, info.PageCount)
            : info.PageCount;

        void Report(int pageNumber, string message) =>
            progress?.Report(new ConversionProgress(pageNumber, pageCount, message));

        if (pageCount < info.PageCount)
        {
            Report(0, $"先頭{pageCount}ページのみ変換します（全{info.PageCount}ページ）。");
        }

        if (options.EnhancePages)
        {
            Report(0, "ページ画像の高品質化（紙色正規化・裏写り抑制）を行います。");
        }

        PageBlockClassifier? classifier = null;
        OcrTextCorrector? corrector = null;

        if (options.Ollama is { } ollama)
        {
            var client = new OllamaClient(ollama.Endpoint);
            if (await client.IsAvailableAsync(cancellationToken))
            {
                classifier = new PageBlockClassifier(client, ollama.Model);
                corrector = new OcrTextCorrector(client, ollama.Model);
                Report(0, $"Ollama({ollama.Model})による意味分類とOCR校正を行います。");
            }
            else
            {
                // 16章「Ollamaに接続できない」: Ollamaなしで処理を継続する。
                Report(0, $"警告: Ollamaサーバー（{ollama.Endpoint}）に接続できません。Ollamaなしで処理を続けます。");
            }
        }

        if (options.ForceOcr)
        {
            Report(0, "PDFのテキスト層を使わず、全ページをOCR（PP-OCRv6多言語モデル）で再認識します（--force-ocr）。");
        }
        else
        {
            var pagesNeedingOcr = info.Pages.Take(pageCount).Count(p => !p.HasText);
            if (pagesNeedingOcr > 0)
            {
                var ocrPurpose = options.PreserveAllTextLines
                    ? "認識文字と行座標を透明テキスト層に使用します。"
                    : "認識文字と行座標から見出し・段組みを推定します。";
                Report(0, $"{pagesNeedingOcr}/{pageCount} ページにテキストレイヤーがありません。OCR（PP-OCRv6多言語モデル）で{ocrPurpose}");
            }
        }

        Directory.CreateDirectory(workDirectory);

        PageOcrService? ocrService = null;
        var ocrUnavailable = false;

        try
        {
            var pages = new List<DocumentPage>();
            var reviewRequiredCount = 0;
            var detectedPageModes = new List<WritingMode>();

            string PageImagePath(int pageNumber) =>
                Path.Combine(workDirectory, $"page-original-{pageNumber:0000}.png");

            // 高品質化の補正値は書籍全体の紙色から決める。そのため先に全ページを
            // ラスタライズして紙色を測る（この時点で書き出した画像を本処理でも使う）。
            PageEnhanceProfile? enhanceProfile = null;
            var prerendered = false;
            if (pageEnhancer is not null)
            {
                Report(0, "紙色を測っています...");
                var stats = new List<PageEnhanceStats>();

                for (var i = 0; i < pageCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pageNumber = i + 1;
                    var imagePath = PageImagePath(pageNumber);
                    pdfService.RenderPageToPng(options.InputPath, pageNumber, imagePath, options.Dpi);

                    try
                    {
                        stats.Add(pageEnhancer.Analyze(imagePath));
                    }
                    catch (Exception ex)
                    {
                        Report(pageNumber, $"警告: 紙色を測れませんでした（{ex.Message}）。");
                    }
                }

                prerendered = true;
                enhanceProfile = PageImageEnhancer.BuildProfile(stats);
                Report(0, enhanceProfile is null
                    ? "紙面と判定できるページが少ないため、ページごとの紙色で高品質化します。"
                    : $"書籍全体の紙色（輝度{enhanceProfile.PaperLuminance:0}）を基準に高品質化します。");
            }

            for (var i = 0; i < pageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pageNumber = i + 1;
                Report(pageNumber, $"ページ {pageNumber}/{pageCount} を処理しています...");

                var imagePath = PageImagePath(pageNumber);
                if (!prerendered)
                {
                    pdfService.RenderPageToPng(options.InputPath, pageNumber, imagePath, options.Dpi);
                }

                // 高品質化: 紙色正規化・裏写り抑制を適用した画像を、表示（EPUB収録）と
                // OCR入力の両方に使う。幾何変換を含まないため座標には影響しない。
                var displayImagePath = imagePath;
                if (pageEnhancer is not null)
                {
                    try
                    {
                        var enhanceResult = pageEnhancer.Enhance(
                            imagePath,
                            Path.Combine(workDirectory, $"page-enhanced-{pageNumber:0000}.png"),
                            enhanceProfile);
                        if (enhanceResult.Applied)
                        {
                            displayImagePath = enhanceResult.ImagePath;
                            Report(pageNumber, $"  紙面を高品質化しました（紙{enhanceResult.PaperLuminance:0}・インク{enhanceResult.InkLuminance:0}）。");
                        }
                    }
                    catch (Exception ex)
                    {
                        Report(pageNumber, $"警告: 高品質化に失敗しました（{ex.Message}）。元画像を使用します。");
                    }
                }

                IReadOnlyList<PpDocLayoutRegion> externalLayoutRegions = [];
                if (!options.PreserveAllTextLines
                    && ppDocLayoutService is not null
                    && ShouldRunExternalBackend(pageNumber))
                {
                    try
                    {
                        externalLayoutRegions = await ppDocLayoutService.AnalyzePageAsync(
                            displayImagePath,
                            Path.Combine(workDirectory, "pp-doc-layout", $"page-{pageNumber:0000}"),
                            cancellationToken);
                        Report(pageNumber, $"  PP-DocLayoutV2から{externalLayoutRegions.Count}件の領域候補を取得しました。");
                    }
                    catch (ExternalBackendException ex)
                    {
                        Report(pageNumber, $"警告: PP-DocLayoutV2を利用できません（{ex.Message}）。既存の図版検出で続けます。");
                    }
                }

                var pageInfo = info.Pages[i];
                var pageBlocks = new List<PageBlock>();
                string? fallbackPdfText = null;
                var requiresOcr = !pageInfo.HasText || options.ForceOcr;
                var pageLayoutProfile = DefaultLayoutProfile();
                var pageWritingMode = pageLayoutProfile.WritingMode;

                if (pageInfo.HasText && !options.ForceOcr)
                {
                    var rawText = pdfService.ExtractPageText(options.InputPath, pageNumber);
                    var textLines = pdfService.ExtractTextLines(options.InputPath, pageNumber);
                    var assessment = textLayerEvaluator.Assess(rawText, textLines);

                    if (assessment.IsUsable)
                    {
                        pageLayoutProfile = ResolveLayoutProfile(textLines);
                        textLines = AssignExternalReadingOrder(pageNumber, textLines, pageLayoutProfile, externalLayoutRegions);
                        pageWritingMode = pageLayoutProfile.WritingMode;
                        detectedPageModes.Add(pageWritingMode);
                        ReportLayout(pageNumber, pageLayoutProfile);
                        pageBlocks = BuildTextBlocks(pageNumber, displayImagePath, textLines, pageLayoutProfile, externalLayoutRegions);
                    }
                    else
                    {
                        requiresOcr = true;
                        fallbackPdfText = rawText;
                        Report(pageNumber, $"  PDF文字レイヤーを使用しません: {assessment.Reason} OCRへ切り替えます。");
                    }
                }

                if (requiresOcr && !ocrUnavailable)
                {
                    try
                    {
                        ocrService ??= new PageOcrService();
                        await ocrService.InitializeAsync(new OcrModelProvisioner(), cancellationToken);

                        // 9.3 前処理: 傾きを補正した画像でOCRし、座標は表示画像の座標系へ戻す。
                        // 傾き補正済み画像はOCR専用で、表示・EPUB出力には使わない（原型保証）。
                        var ocrInputPath = displayImagePath;
                        OcrPreprocessResult? preprocess = null;
                        try
                        {
                            preprocess = ocrPreprocessor.Preprocess(
                                displayImagePath,
                                Path.Combine(workDirectory, $"page-deskewed-{pageNumber:0000}.png"));
                            if (preprocess.DeskewApplied)
                            {
                                ocrInputPath = preprocess.ImagePathForOcr;
                                Report(pageNumber, $"  傾き{preprocess.SkewAngleDegrees:+0.0;-0.0}°を補正した画像でOCRします。");
                            }
                        }
                        catch (Exception ex)
                        {
                            // 前処理は精度向上のための補助であり、失敗しても元画像でOCRを続行する。
                            Report(pageNumber, $"警告: OCR前処理に失敗しました（{ex.Message}）。元画像でOCRします。");
                        }

                        var ocrResult = ocrService.RecognizePage(ocrInputPath);
                        var ocrLines = ocrResult.Lines;

                        if (preprocess is { DeskewApplied: true })
                        {
                            ocrLines = ocrLines
                                .Select(line => line with { Bounds = preprocess.MapToOriginal(line.Bounds) })
                                .ToList();
                        }

                        if (ocrResult.DroppedLineCount > 0)
                        {
                            Report(pageNumber, $"  低信頼のOCRゴミ行{ocrResult.DroppedLineCount}件を除外しました。");
                        }

                        var rapidLayoutProfile = ResolveLayoutProfile(ocrLines);
                        if (!options.PreserveAllTextLines
                            && ndlOcrService is not null
                            && options.NdlOcr is { } ndlOptions
                            && ShouldRunExternalBackend(pageNumber)
                            && rapidLayoutProfile.WritingMode == WritingMode.Vertical)
                        {
                            try
                            {
                                var ndlResult = await ndlOcrService.RecognizePageAsync(
                                    displayImagePath,
                                    Path.Combine(workDirectory, "ndlocr", $"page-{pageNumber:0000}"),
                                    cancellationToken);
                                if (ndlResult.AverageConfidence >= ndlOptions.MinimumAverageConfidence
                                    && ndlResult.VerticalLineShare >= ndlOptions.MinimumVerticalLineShare)
                                {
                                    ocrLines = ndlResult.Lines;
                                    Report(
                                        pageNumber,
                                        $"  NDLOCR-Liteの{ocrLines.Count}行を使用します"
                                        + $"（平均信頼度{ndlResult.AverageConfidence:0.00}、縦書き{ndlResult.VerticalLineShare:P0}）。");
                                }
                                else
                                {
                                    Report(
                                        pageNumber,
                                        $"  NDLOCR-Lite結果を採用しません"
                                        + $"（平均信頼度{ndlResult.AverageConfidence:0.00}、縦書き{ndlResult.VerticalLineShare:P0}）。RapidOCRへ戻します。");
                                }
                            }
                            catch (ExternalBackendException ex)
                            {
                                Report(pageNumber, $"警告: NDLOCR-Liteを利用できません（{ex.Message}）。RapidOCRへ戻します。");
                            }
                        }

                        pageLayoutProfile = ResolveLayoutProfile(ocrLines);
                        ocrLines = AssignExternalReadingOrder(pageNumber, ocrLines, pageLayoutProfile, externalLayoutRegions);
                        pageWritingMode = pageLayoutProfile.WritingMode;
                        detectedPageModes.Add(pageWritingMode);
                        ReportLayout(pageNumber, pageLayoutProfile);
                        pageBlocks = BuildTextBlocks(pageNumber, displayImagePath, ocrLines, pageLayoutProfile, externalLayoutRegions);
                    }
                    catch (Exception ex) when (ex is OcrModelDownloadException or InvalidOperationException)
                    {
                        // 16章「OCRモデルがない」: OCRなしで処理を継続する。
                        Report(pageNumber, $"警告: OCRを利用できません（{ex.Message}）。");
                        ocrUnavailable = true;
                    }
                }

                // 座標付きPDF文字が使えず、OCRも利用できない場合だけ、検索可能性を完全に
                // 失わないために座標なしのPDF文字を要確認ブロックとして残す。
                if (pageBlocks.Count == 0 && !string.IsNullOrWhiteSpace(fallbackPdfText))
                {
                    pageBlocks.Add(new PageBlock
                    {
                        Id = $"p{pageNumber:0000}-b0001",
                        PageNumber = pageNumber,
                        Bounds = new BoundingBox(0, 0, 1, 1),
                        Type = BlockType.Body,
                        OcrText = fallbackPdfText,
                        OcrConfidence = 0.5,
                        TextSource = TextSourceKind.PdfTextLayer,
                        ReadingOrder = 0,
                        RequiresReview = true,
                    });
                    Report(pageNumber, "  警告: OCR結果がないため、座標なしのPDF文字を要確認として使用します。");
                }

                reviewRequiredCount += pageBlocks.Count(b => b.RequiresReview);

                var page = new DocumentPage
                {
                    PageNumber = pageNumber,
                    OriginalImagePath = imagePath,
                    ProcessedImagePath = displayImagePath,
                    PreviewImagePath = displayImagePath,
                    Width = pageInfo.WidthPoints,
                    Height = pageInfo.HeightPoints,
                    WritingMode = pageWritingMode,
                    LayoutPattern = pageLayoutProfile.Pattern,
                    LayoutPatternConfidence = pageLayoutProfile.Confidence,
                    Status = pageBlocks.Count > 0 ? PageProcessingStatus.OcrCompleted : PageProcessingStatus.Error,
                };
                page.Blocks.AddRange(pageBlocks);

                if (classifier is not null && pageBlocks.Count > 0)
                {
                    try
                    {
                        var changedCount = await classifier.ClassifyPageAsync(page, cancellationToken);
                        if (changedCount > 0)
                        {
                            Report(pageNumber, $"  Ollamaにより{changedCount}件のブロック分類を更新しました。");
                        }
                    }
                    catch (OllamaClassificationException ex)
                    {
                        // 16章「Ollama応答不正」: 規則ベースの結果をそのまま使用し、処理を継続する。
                        Report(pageNumber, $"警告: Ollamaによる分類に失敗しました（{ex.Message}）。規則ベースの分類のまま続けます。");
                    }
                }

                if (corrector is not null && pageBlocks.Count > 0)
                {
                    try
                    {
                        var correctedCount = await corrector.CorrectPageAsync(page, cancellationToken);
                        if (correctedCount > 0)
                        {
                            Report(pageNumber, $"  Ollamaにより{correctedCount}件のOCR文字列を校正しました。");
                        }
                    }
                    catch (OllamaClassificationException ex)
                    {
                        // OCR校正は補助機能であり、失敗してもOCR結果のまま処理を継続する。
                        Report(pageNumber, $"警告: OllamaによるOCR校正に失敗しました（{ex.Message}）。OCR結果のまま続けます。");
                    }
                }

                pages.Add(page);
            }

            if (!options.PreserveAllTextLines)
            {
                var marginChangeCount = new RepeatedMarginClassifier().Classify(pages);
                if (marginChangeCount > 0)
                {
                    Report(pageCount, $"全ページの反復を照合し、柱・フッター・ノンブル候補{marginChangeCount}件を再分類しました。");
                }

                var sourceTocExcludedCount = new SourceTableOfContentsClassifier().Classify(pages);
                if (sourceTocExcludedCount > 0)
                {
                    Report(pageCount, $"原PDF内の印刷目次{sourceTocExcludedCount}ブロックをEPUB本文から除外しました。");
                }

                var articleStructure = new ArticleStructureClassifier().Classify(pages);
                if (articleStructure.ArticleTitles > 0
                    || articleStructure.DemotedFalseHeadings > 0
                    || articleStructure.ExcludedFurniture > 0)
                {
                    Report(
                        pageCount,
                        $"記事構造: 記事タイトル{articleStructure.ArticleTitles}件、著者・所属・要旨{articleStructure.MetadataBlocks}件、"
                        + $"本文断片の誤見出し{articleStructure.DemotedFalseHeadings}件と柱・号数{articleStructure.ExcludedFurniture}件を補正しました。");
                }

                var crossPageMergeCount = paragraphMerger.MergeAcrossPages(pages);
                if (crossPageMergeCount > 0)
                {
                    Report(pageCount, $"改ページをまたぐ本文段落{crossPageMergeCount}件を連結しました。");
                }
            }

            if (reviewRequiredCount > 0)
            {
                Report(pageCount,$"{reviewRequiredCount} 件のブロックがOCR信頼度0.85未満のため要確認です。");
            }

            var pdfTextPageCount = pages.Count(page => page.Blocks.Any(block => block.TextSource == TextSourceKind.PdfTextLayer));
            var ocrTextPageCount = pages.Count(page => page.Blocks.Any(block => block.TextSource == TextSourceKind.Ocr));
            var ndlOcrTextPageCount = pages.Count(page => page.Blocks.Any(block => block.TextSource == TextSourceKind.NdlOcr));
            var noTextPageCount = pages.Count(page => page.Blocks.All(block => block.TextSource == TextSourceKind.Unknown));
            Report(
                pageCount,
                $"文字情報の取得元: PDF {pdfTextPageCount}ページ / RapidOCR {ocrTextPageCount}ページ"
                + $" / NDLOCR-Lite {ndlOcrTextPageCount}ページ / 文字なし {noTextPageCount}ページ");

            // 綴じ方向は縦書きページの多数決で決める（強制指定があればそれに従う）。
            var documentMode = options.WritingMode switch
            {
                WritingModeSetting.Vertical => WritingMode.Vertical,
                WritingModeSetting.Horizontal => WritingMode.Horizontal,
                _ => WritingModeDetector.DetectDocumentMode(detectedPageModes),
            };

            if (options.WritingMode == WritingModeSetting.Auto && documentMode == WritingMode.Vertical)
            {
                var verticalPages = detectedPageModes.Count(m => m == WritingMode.Vertical);
                Report(pageCount,$"縦書きと判定しました（縦書き{verticalPages}/横書き{detectedPageModes.Count - verticalPages}ページ）。右綴じで出力します。");
            }

            // 言語は認識テキストの文字種から自動判定する（--languageで強制可能）。
            var language = options.Language ?? LanguageDetector.Detect(
                pages.SelectMany(p => p.Blocks).Select(b => b.CorrectedText ?? b.OcrText));
            if (options.Language is null)
            {
                Report(pageCount,$"言語: {language}（自動判定）");
            }

            var project = new EpubFabricProject
            {
                Id = Guid.NewGuid(),
                Title = Path.GetFileNameWithoutExtension(options.InputPath),
                SourcePdfPath = options.InputPath,
                Language = language,
                WritingMode = documentMode,
                Pages = pages,
            };

            return (project, pages);
        }
        finally
        {
            ocrService?.Dispose();
        }

        PageLayoutProfile DefaultLayoutProfile() => new(
            options.WritingMode == WritingModeSetting.Vertical
                ? PageLayoutPattern.VerticalSingleColumn
                : PageLayoutPattern.HorizontalSingleColumn,
            0);

        // 書字方向を決めた後、同じ読み座標上で1段・2段を明示分類する。
        PageLayoutProfile ResolveLayoutProfile(IReadOnlyList<TextLine> lines) =>
            PageLayoutPatternDetector.Detect(
                lines,
                options.WritingMode switch
                {
                    WritingModeSetting.Vertical => WritingMode.Vertical,
                    WritingModeSetting.Horizontal => WritingMode.Horizontal,
                    _ => null,
                });

        void ReportLayout(int pageNumber, PageLayoutProfile profile)
        {
            var direction = profile.WritingMode == WritingMode.Vertical ? "縦書き" : "横書き";
            var columns = profile.UsesGenericColumnDetection ? "複雑段組み" : $"{profile.ColumnCount}段";
            Report(pageNumber, $"  レイアウト: {direction}{columns}（信頼度{profile.Confidence:0.00}）");
        }

        bool ShouldRunExternalBackend(int pageNumber) =>
            options.ExternalBackendPages is null || options.ExternalBackendPages.Contains(pageNumber);

        IReadOnlyList<TextLine> AssignExternalReadingOrder(
            int pageNumber,
            IReadOnlyList<TextLine> lines,
            PageLayoutProfile layoutProfile,
            IReadOnlyList<PpDocLayoutRegion> externalLayoutRegions)
        {
            if (options.PpDocLayout is not { } ppOptions
                || externalLayoutRegions.Count == 0
                || lines.Any(line => line.SourceReadingOrder is not null))
            {
                return lines;
            }

            var assignment = PpDocLayoutReadingOrderAssigner.Assign(
                lines,
                externalLayoutRegions,
                layoutProfile.WritingMode,
                ppOptions.MinimumTextLineCoverage);
            if (assignment.Adopted)
            {
                Report(
                    pageNumber,
                    $"  PP-DocLayoutV2の領域読み順を採用しました"
                    + $"（文字行{assignment.AssignedLineCount}/{lines.Count}、{assignment.Coverage:P0}）。");
            }
            else
            {
                Report(
                    pageNumber,
                    $"  PP-DocLayoutV2の領域読み順を採用しません"
                    + $"（文字行{assignment.AssignedLineCount}/{lines.Count}、{assignment.Coverage:P0}）。");
            }

            return assignment.Lines;
        }

        // 固定レイアウトでは全テキスト行を座標付きのまま保持する。
        // リフロー型ではレイアウト解析と段落統合を適用し、図ブロックの画像を切り出す。
        List<PageBlock> BuildTextBlocks(
            int pageNumber,
            string imagePath,
            IReadOnlyList<TextLine> lines,
            PageLayoutProfile layoutProfile,
            IReadOnlyList<PpDocLayoutRegion> externalLayoutRegions) =>
            options.PreserveAllTextLines
                ? textLayerBlockBuilder.Build(pageNumber, lines, layoutProfile.WritingMode, layoutProfile)
                : AnalyzeLayout(pageNumber, imagePath, lines, layoutProfile, externalLayoutRegions);

        List<PageBlock> AnalyzeLayout(
            int pageNumber,
            string imagePath,
            IReadOnlyList<TextLine> lines,
            PageLayoutProfile layoutProfile,
            IReadOnlyList<PpDocLayoutRegion> externalLayoutRegions)
        {
            // 太字見出し検出用に行のインク密度を測る（高さが本文と同じゴシック見出し対策）。
            lines = inkDensityMeasurer.Measure(imagePath, lines);

            var textBounds = lines.Select(l => l.Bounds).ToList();
            var regions = regionDetector.DetectRegions(imagePath, textBounds);
            var externalFigureCount = options.PpDocLayout is { } ppOptions
                ? ExternalFigureRegionMerger.Merge(
                    regions,
                    externalLayoutRegions,
                    ppOptions.MinimumStandaloneImageConfidence)
                : 0;
            if (externalFigureCount > 0)
            {
                Report(pageNumber, $"  PP-DocLayoutV2の高信頼図版候補{externalFigureCount}件を追加しました。");
            }
            var blocks = paragraphMerger.Merge(
                layoutAnalyzer.AnalyzePage(
                    pageNumber,
                    lines,
                    regions,
                    layoutProfile.WritingMode,
                    layoutProfile),
                layoutProfile.WritingMode);

            foreach (var figureBlock in blocks.Where(b => b.Type == BlockType.Figure))
            {
                // 図版はJPEGで抽出する（無圧縮PNGはリフローEPUBを不必要に大きくする）。
                var figureImagePath = Path.Combine(workDirectory, $"{figureBlock.Id}.jpg");
                figureExtractor.Extract(imagePath, figureBlock.Bounds, figureImagePath);
                figureBlock.ExtractedImagePath = figureImagePath;
            }

            return blocks;
        }

    }

    /// <summary>構築済みプロジェクトからEPUBを書き出す。</summary>
    /// <param name="coverPageAsImage">
    /// リフロー型で1ページ目をテキスト化せず表紙画像として収録する。固定レイアウトでは
    /// 元から全ページが画像のため無視される。
    /// </param>
    public void BuildEpub(
        EpubFabricProject project,
        OutputLayout layout,
        string outputPath,
        PageImageEncodingOptions? imageOptions = null,
        bool coverPageAsImage = false)
    {
        imageOptions ??= new PageImageEncodingOptions();

        if (layout == OutputLayout.Fixed)
        {
            new FixedLayoutEpubPackageBuilder(imageOptions.JpegQuality, imageOptions.MaxSideLength).Build(project, outputPath);
            return;
        }

        // 表紙を画像で収録するなら、1ページ目の文字は本文へ入れない（表紙のOCRは
        // 装飾文字の誤読が多く、そのまま本文化すると第1章の先頭が荒れる）。
        var textPages = coverPageAsImage && project.Pages.Count > 1
            ? project.Pages.OrderBy(p => p.PageNumber).Skip(1).ToList()
            : project.Pages;

        var chapters = new DocumentBuilder().BuildChapters(textPages, project.Title);
        var blocksById = textPages.SelectMany(p => p.Blocks).ToDictionary(b => b.Id);

        var coverTranscoder = coverPageAsImage
            ? new PageImageTranscoder(imageOptions.JpegQuality, imageOptions.MaxSideLength)
            : null;

        new EpubPackageBuilder(coverTranscoder).Build(project, chapters, blocksById, outputPath);
    }
}
