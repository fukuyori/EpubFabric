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
        var pageEnhancer = options.EnhancePages ? new PageImageEnhancer() : null;
        var info = pdfService.GetInfo(options.InputPath);

        void Report(int pageNumber, string message) =>
            progress?.Report(new ConversionProgress(pageNumber, info.PageCount, message));

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

        var pagesNeedingOcr = info.Pages.Count(p => !p.HasText);
        if (pagesNeedingOcr > 0)
        {
            var ocrPurpose = options.PreserveAllTextLines
                ? "認識文字と行座標を透明テキスト層に使用します。"
                : "認識文字と行座標から見出し・段組みを推定します。";
            Report(0, $"{pagesNeedingOcr}/{info.PageCount} ページにテキストレイヤーがありません。OCR（PP-OCRv6多言語モデル）で{ocrPurpose}");
        }

        Directory.CreateDirectory(workDirectory);

        PageOcrService? ocrService = null;
        var ocrUnavailable = false;

        try
        {
            var pages = new List<DocumentPage>();
            var reviewRequiredCount = 0;

            for (var i = 0; i < info.PageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pageNumber = i + 1;
                Report(pageNumber, $"ページ {pageNumber}/{info.PageCount} を処理しています...");

                var imagePath = Path.Combine(workDirectory, $"page-original-{pageNumber:0000}.png");
                pdfService.RenderPageToPng(options.InputPath, pageNumber, imagePath, options.Dpi);

                // 高品質化: 紙色正規化・裏写り抑制を適用した画像を、表示（EPUB収録）と
                // OCR入力の両方に使う。幾何変換を含まないため座標には影響しない。
                var displayImagePath = imagePath;
                if (pageEnhancer is not null)
                {
                    try
                    {
                        var enhanceResult = pageEnhancer.Enhance(
                            imagePath,
                            Path.Combine(workDirectory, $"page-enhanced-{pageNumber:0000}.png"));
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

                var pageInfo = info.Pages[i];
                var pageBlocks = new List<PageBlock>();
                string? fallbackPdfText = null;
                var requiresOcr = !pageInfo.HasText;

                if (pageInfo.HasText)
                {
                    var rawText = pdfService.ExtractPageText(options.InputPath, pageNumber);
                    var textLines = pdfService.ExtractTextLines(options.InputPath, pageNumber);
                    var assessment = textLayerEvaluator.Assess(rawText, textLines);

                    if (assessment.IsUsable)
                    {
                        pageBlocks = BuildTextBlocks(pageNumber, displayImagePath, textLines);
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

                        pageBlocks = BuildTextBlocks(pageNumber, displayImagePath, ocrLines);
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
                    WritingMode = WritingMode.Horizontal,
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

            if (reviewRequiredCount > 0)
            {
                Report(info.PageCount, $"{reviewRequiredCount} 件のブロックがOCR信頼度0.85未満のため要確認です。");
            }

            var pdfTextPageCount = pages.Count(page => page.Blocks.Any(block => block.TextSource == TextSourceKind.PdfTextLayer));
            var ocrTextPageCount = pages.Count(page => page.Blocks.Any(block => block.TextSource == TextSourceKind.Ocr));
            var noTextPageCount = pages.Count(page => page.Blocks.All(block => block.TextSource == TextSourceKind.Unknown));
            Report(info.PageCount, $"文字情報の取得元: PDF {pdfTextPageCount}ページ / OCR {ocrTextPageCount}ページ / 文字なし {noTextPageCount}ページ");

            var project = new EpubFabricProject
            {
                Id = Guid.NewGuid(),
                Title = Path.GetFileNameWithoutExtension(options.InputPath),
                SourcePdfPath = options.InputPath,
                Pages = pages,
            };

            return (project, pages);
        }
        finally
        {
            ocrService?.Dispose();
        }

        // 固定レイアウトでは全テキスト行を座標付きのまま保持する。
        // リフロー型ではレイアウト解析と段落統合を適用し、図ブロックの画像を切り出す。
        List<PageBlock> BuildTextBlocks(int pageNumber, string imagePath, IReadOnlyList<TextLine> lines) =>
            options.PreserveAllTextLines
                ? textLayerBlockBuilder.Build(pageNumber, lines)
                : AnalyzeLayout(pageNumber, imagePath, lines);

        List<PageBlock> AnalyzeLayout(int pageNumber, string imagePath, IReadOnlyList<TextLine> lines)
        {
            var textBounds = lines.Select(l => l.Bounds).ToList();
            var regions = regionDetector.DetectRegions(imagePath, textBounds);
            var blocks = paragraphMerger.Merge(layoutAnalyzer.AnalyzePage(pageNumber, lines, regions));

            foreach (var figureBlock in blocks.Where(b => b.Type == BlockType.Figure))
            {
                var figureImagePath = Path.Combine(workDirectory, $"{figureBlock.Id}.png");
                figureExtractor.Extract(imagePath, figureBlock.Bounds, figureImagePath);
                figureBlock.ExtractedImagePath = figureImagePath;
            }

            return blocks;
        }
    }

    /// <summary>構築済みプロジェクトからEPUBを書き出す。</summary>
    public void BuildEpub(
        EpubFabricProject project,
        OutputLayout layout,
        string outputPath,
        PageImageEncodingOptions? imageOptions = null)
    {
        if (layout == OutputLayout.Fixed)
        {
            imageOptions ??= new PageImageEncodingOptions();
            new FixedLayoutEpubPackageBuilder(imageOptions.JpegQuality, imageOptions.MaxSideLength).Build(project, outputPath);
            return;
        }

        var chapters = new DocumentBuilder().BuildChapters(project.Pages, project.Title);
        var blocksById = project.Pages.SelectMany(p => p.Blocks).ToDictionary(b => b.Id);
        new EpubPackageBuilder().Build(project, chapters, blocksById, outputPath);
    }
}
