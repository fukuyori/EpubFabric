using System.Text;
using EpubFabric.Core.Models;
using EpubFabric.Document;
using EpubFabric.Epub;
using EpubFabric.Evaluation;
using EpubFabric.Imaging;
using EpubFabric.Layout;
using EpubFabric.Ocr;
using EpubFabric.Ollama;
using EpubFabric.Pdf;
using EpubFabric.Persistence;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    return args[0] switch
    {
        "convert" => await RunConvert(args),
        "evaluate" => await RunEvaluate(args),
        "analyze" => await RunAnalyze(args),
        "export" => RunExport(args),
        "info" => RunInfo(args),
        _ => Unknown(),
    };
}
catch (PdfLoadException ex)
{
    Console.Error.WriteLine($"エラー: {ex.Message}");
    return 1;
}
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine($"エラー: {ex.Message}");
    return 1;
}

static int Unknown()
{
    PrintUsage();
    return 1;
}

static async Task<int> RunConvert(string[] args)
{
    var (inputPath, options) = ParseOptions(args);
    if (inputPath is null || !RequireExistingFile(inputPath))
    {
        return 1;
    }

    var outputPath = options.GetValueOrDefault("--output") ?? Path.ChangeExtension(inputPath, ".epub");
    var dpi = ParseDpi(options);
    var ollamaOptions = ParseOllamaOptions(options);

    var workDirectory = Path.Combine(Path.GetTempPath(), $"epubfabric-{Guid.NewGuid():N}");
    var (project, pages) = await BuildProjectFromPdf(inputPath, workDirectory, dpi, ollamaOptions);

    var chapters = new DocumentBuilder().BuildChapters(pages, project.Title);
    var blocksById = pages.SelectMany(p => p.Blocks).ToDictionary(b => b.Id);
    new EpubPackageBuilder().Build(project, chapters, blocksById, outputPath);

    Console.WriteLine($"EPUBを生成しました: {outputPath}");
    return 0;
}

static async Task<int> RunEvaluate(string[] args)
{
    var (inputPath, options) = ParseOptions(args);
    if (inputPath is null || !RequireExistingFile(inputPath))
    {
        return 1;
    }

    var reportDirectory = options.GetValueOrDefault("--report")
        ?? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(inputPath)) ?? ".",
            $"{Path.GetFileNameWithoutExtension(inputPath)}-report");
    var dpi = ParseDpi(options);
    var ollamaOptions = ParseOllamaOptions(options);

    var workDirectory = Path.Combine(Path.GetTempPath(), $"epubfabric-{Guid.NewGuid():N}");
    var (project, pages) = await BuildProjectFromPdf(inputPath, workDirectory, dpi, ollamaOptions);

    var blocksById = pages.SelectMany(p => p.Blocks).ToDictionary(b => b.Id);
    var summary = new LayoutEvaluator().Evaluate(pages);

    Console.WriteLine("評価レポートを生成しています...");

    var overlayRenderer = new BlockOverlayRenderer();
    var xhtmlGenerator = new EpubXhtmlGenerator();
    var pagesDirectory = Path.Combine(reportDirectory, "pages");
    var imagesDirectory = Path.Combine(reportDirectory, "images");
    Directory.CreateDirectory(pagesDirectory);

    var entries = new List<PageReportEntry>();

    foreach (var page in pages.OrderBy(p => p.PageNumber))
    {
        var overlayRelativePath = $"pages/page-{page.PageNumber:0000}.jpg";
        overlayRenderer.Render(page.OriginalImagePath, page.Blocks, Path.Combine(reportDirectory, overlayRelativePath));

        // EPUB断片は変換時と同じ規則で生成する（除外ブロックを落とし、読み順に並べる）。
        var blockIds = page.Blocks
            .Where(b => !b.IsExcluded)
            .OrderBy(b => b.ReadingOrder)
            .Select(b => b.Id)
            .ToList();
        var fragmentHtml = string.Concat(
            xhtmlGenerator.GenerateBlockElements(blockIds, blocksById)
                .Select(e => e.ToString(System.Xml.Linq.SaveOptions.None)));
        // EPUB内の相対パス（../images/）をレポート内のパスへ読み替える。
        fragmentHtml = fragmentHtml.Replace("src=\"../images/", "src=\"images/");

        entries.Add(new PageReportEntry(summary.Pages[entries.Count], overlayRelativePath, fragmentHtml));
    }

    foreach (var imagePath in pages.SelectMany(p => p.Blocks)
        .Where(b => b.ExtractedImagePath is not null)
        .Select(b => b.ExtractedImagePath!)
        .Distinct())
    {
        Directory.CreateDirectory(imagesDirectory);
        File.Copy(imagePath, Path.Combine(imagesDirectory, Path.GetFileName(imagePath)), overwrite: true);
    }

    new EvaluationReportBuilder().Write(reportDirectory, project.Title, summary, entries);

    Console.WriteLine($"評価レポートを生成しました: {Path.Combine(reportDirectory, "index.html")}");
    Console.WriteLine($"  解析済ページ       : {summary.PagesWithBlocks}/{summary.PageCount}");
    Console.WriteLine($"  テキスト網羅率     : {summary.TextCoverage:P1}（欠落 {summary.TextCharsDropped:N0} 字）");
    Console.WriteLine($"  図版の画像化       : {summary.FigureWithImageCount}/{summary.FigureCount}");
    Console.WriteLine($"  見出し検出         : {summary.HeadingCount} 件");
    Console.WriteLine($"  低信頼ブロック混入 : {summary.LowConfidenceIncludedCount} 件");
    return 0;
}

static async Task<int> RunAnalyze(string[] args)
{
    var (inputPath, options) = ParseOptions(args);
    if (inputPath is null || !RequireExistingFile(inputPath))
    {
        return 1;
    }

    if (!options.TryGetValue("--project", out var projectDirectory))
    {
        Console.Error.WriteLine("エラー: --project <book.efproj> を指定してください。");
        return 1;
    }

    var dpi = ParseDpi(options);
    var ollamaOptions = ParseOllamaOptions(options);
    var workDirectory = Path.Combine(Path.GetTempPath(), $"epubfabric-{Guid.NewGuid():N}");
    var (project, _) = await BuildProjectFromPdf(inputPath, workDirectory, dpi, ollamaOptions);

    new EfprojStore().Save(project, projectDirectory);

    Console.WriteLine($"プロジェクトを保存しました: {projectDirectory}");
    Console.WriteLine($"blocks/text 内のテキストファイルを編集して校正できます。校正後は次を実行してください:");
    Console.WriteLine($"  epubfabric export {projectDirectory} --format epub");
    return 0;
}

static int RunExport(string[] args)
{
    var (projectDirectory, options) = ParseOptions(args);
    if (projectDirectory is null)
    {
        Console.Error.WriteLine("エラー: プロジェクト（.efproj）を指定してください。");
        return 1;
    }

    if (!Directory.Exists(projectDirectory))
    {
        Console.Error.WriteLine($"エラー: プロジェクトフォルダーが見つかりません: {projectDirectory}");
        return 1;
    }

    var format = options.GetValueOrDefault("--format", "epub");
    if (format != "epub")
    {
        Console.Error.WriteLine($"エラー: 現時点では --format epub のみ対応しています（指定値: {format}）。");
        return 1;
    }

    var outputPath = options.GetValueOrDefault("--output")
        ?? Path.ChangeExtension(projectDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), ".epub");

    var project = new EfprojStore().Load(projectDirectory);
    var chapters = new DocumentBuilder().BuildChapters(project.Pages, project.Title);
    var blocksById = project.Pages.SelectMany(p => p.Blocks).ToDictionary(b => b.Id);

    var correctedCount = blocksById.Values.Count(b => b.IsManuallyEdited);
    if (correctedCount > 0)
    {
        Console.WriteLine($"{correctedCount} 件の校正済みブロックを反映します。");
    }

    new EpubPackageBuilder().Build(project, chapters, blocksById, outputPath);

    Console.WriteLine($"EPUBを生成しました: {outputPath}");
    return 0;
}

static int RunInfo(string[] args)
{
    var (inputPath, _) = ParseOptions(args);
    if (inputPath is null || !RequireExistingFile(inputPath))
    {
        return 1;
    }

    var fileSize = new FileInfo(inputPath).Length;
    var info = new PdfDocumentService().GetInfo(inputPath);
    var textPageCount = info.Pages.Count(p => p.HasText);

    Console.WriteLine($"ファイル: {inputPath}");
    Console.WriteLine($"ファイルサイズ: {fileSize:N0} bytes");
    Console.WriteLine($"PDFバージョン: {info.PdfVersion}");
    Console.WriteLine($"ページ数: {info.PageCount}");
    Console.WriteLine($"テキストレイヤー: {(info.HasTextLayer ? $"あり（{textPageCount}/{info.PageCount}ページ）" : "なし")}");

    if (info.Pages.Count > 0)
    {
        var first = info.Pages[0];
        Console.WriteLine($"1ページ目のサイズ: {first.WidthPoints} x {first.HeightPoints} pt");
    }

    return 0;
}

static async Task<(EpubFabricProject Project, List<DocumentPage> Pages)> BuildProjectFromPdf(
    string inputPath, string workDirectory, int dpi, OllamaOptions? ollamaOptions = null)
{
    var pdfService = new PdfDocumentService();
    var layoutAnalyzer = new HeuristicLayoutAnalyzer();
    var paragraphMerger = new ParagraphMerger();
    var regionDetector = new NonTextRegionDetector();
    var figureExtractor = new FigureImageExtractor();
    var info = pdfService.GetInfo(inputPath);

    PageBlockClassifier? classifier = null;

    if (ollamaOptions is { Enabled: true })
    {
        var client = new OllamaClient(ollamaOptions.Endpoint);
        if (await client.IsAvailableAsync())
        {
            classifier = new PageBlockClassifier(client, ollamaOptions.Model);
            Console.WriteLine($"Ollama({ollamaOptions.Model})による意味分類を行います。");
        }
        else
        {
            // 16章「Ollamaに接続できない」: Ollamaなしで処理を継続する。
            Console.WriteLine($"警告: Ollamaサーバー（{ollamaOptions.Endpoint}）に接続できません。Ollamaなしで処理を続けます。");
        }
    }

    var pagesNeedingOcr = info.Pages.Count(p => !p.HasText);
    if (pagesNeedingOcr > 0)
    {
        Console.WriteLine($"{pagesNeedingOcr}/{info.PageCount} ページにテキストレイヤーがありません。OCR（PP-OCRv6多言語モデル）で認識し、行座標から見出し・段組みを推定します。");
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
            var pageNumber = i + 1;
            Console.WriteLine($"ページ {pageNumber}/{info.PageCount} を処理しています...");

            var imagePath = Path.Combine(workDirectory, $"page-original-{pageNumber:0000}.png");
            pdfService.RenderPageToPng(inputPath, pageNumber, imagePath, dpi);

            var pageInfo = info.Pages[i];
            var pageBlocks = new List<PageBlock>();

            if (pageInfo.HasText)
            {
                // 9.3: テキストレイヤーの文字座標から行を再構成し、OCR結果と同じ
                // レイアウト解析（見出し・段組み・柱・図版検出）を適用する。
                var textLines = pdfService.ExtractTextLines(inputPath, pageNumber);
                if (textLines.Count > 0)
                {
                    pageBlocks = AnalyzeLayout(pageNumber, imagePath, textLines);
                }
                else
                {
                    // 文字座標を取得できないPDFは、従来どおりページ全体を1つの本文ブロックとして扱う。
                    var text = pdfService.ExtractPageText(inputPath, pageNumber);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        pageBlocks.Add(new PageBlock
                        {
                            Id = $"p{pageNumber:0000}-b0001",
                            PageNumber = pageNumber,
                            Bounds = new BoundingBox(0, 0, 1, 1),
                            Type = BlockType.Body,
                            OcrText = text,
                            OcrConfidence = 1.0,
                            ReadingOrder = 0,
                        });
                    }
                }
            }
            else if (!ocrUnavailable)
            {
                try
                {
                    ocrService ??= new PageOcrService();
                    await ocrService.InitializeAsync(new OcrModelProvisioner());

                    var ocrResult = ocrService.RecognizePage(imagePath);
                    pageBlocks = AnalyzeLayout(pageNumber, imagePath, ocrResult.Lines);
                }
                catch (Exception ex) when (ex is OcrModelDownloadException or InvalidOperationException)
                {
                    // 16章「OCRモデルがない」: OCRなしで処理を継続する。
                    Console.WriteLine($"警告: OCRを利用できません（{ex.Message}）。以降もテキストなしで処理を続けます。");
                    ocrUnavailable = true;
                }
            }

            reviewRequiredCount += pageBlocks.Count(b => b.RequiresReview);

            var page = new DocumentPage
            {
                PageNumber = pageNumber,
                OriginalImagePath = imagePath,
                ProcessedImagePath = imagePath,
                PreviewImagePath = imagePath,
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
                    var changedCount = await classifier.ClassifyPageAsync(page);
                    if (changedCount > 0)
                    {
                        Console.WriteLine($"  Ollamaにより{changedCount}件のブロック分類を更新しました。");
                    }
                }
                catch (OllamaClassificationException ex)
                {
                    // 16章「Ollama応答不正」: 規則ベースの結果をそのまま使用し、処理を継続する。
                    Console.WriteLine($"警告: Ollamaによる分類に失敗しました（{ex.Message}）。規則ベースの分類のまま続けます。");
                }
            }

            pages.Add(page);
        }

        if (reviewRequiredCount > 0)
        {
            Console.WriteLine($"{reviewRequiredCount} 件のブロックがOCR信頼度0.85未満のため要確認です。");
        }

        var project = new EpubFabricProject
        {
            Id = Guid.NewGuid(),
            Title = Path.GetFileNameWithoutExtension(inputPath),
            SourcePdfPath = inputPath,
            Pages = pages,
        };

        return (project, pages);
    }
    finally
    {
        ocrService?.Dispose();
    }

    // 行単位の認識結果（OCR・テキスト層共通）へレイアウト解析と段落統合を適用し、
    // 図ブロックの画像を切り出す。
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

static (string? PositionalArg, Dictionary<string, string> Options) ParseOptions(string[] args)
{
    string? positional = null;
    var options = new Dictionary<string, string>();

    for (var i = 1; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            // 値なしのフラグ（例: --ollama）が直後に別のフラグを取り込んでしまわないよう、
            // 次のトークンが "--" で始まる場合は値として消費しない。
            options[args[i]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : string.Empty;
        }
        else
        {
            positional ??= args[i];
        }
    }

    return (positional, options);
}

static int ParseDpi(Dictionary<string, string> options) =>
    options.TryGetValue("--dpi", out var value) ? int.Parse(value) : 300;

static OllamaOptions ParseOllamaOptions(Dictionary<string, string> options) => new(
    Enabled: options.ContainsKey("--ollama"),
    Endpoint: options.GetValueOrDefault("--ollama-endpoint", "http://localhost:11434"),
    Model: options.GetValueOrDefault("--ollama-model", "gemma4:12b"));

static bool RequireExistingFile(string path)
{
    if (File.Exists(path))
    {
        return true;
    }

    Console.Error.WriteLine($"エラー: ファイルが見つかりません: {path}");
    return false;
}

static void PrintUsage()
{
    Console.WriteLine("使い方:");
    Console.WriteLine("  epubfabric info <input.pdf>");
    Console.WriteLine("  epubfabric convert <input.pdf> [--output <output.epub>] [--dpi <dpi>] [--ollama] [--ollama-model <model>] [--ollama-endpoint <url>]");
    Console.WriteLine("  epubfabric evaluate <input.pdf> [--report <report-dir>] [--dpi <dpi>] [--ollama] [--ollama-model <model>] [--ollama-endpoint <url>]");
    Console.WriteLine("  epubfabric analyze <input.pdf> --project <book.efproj> [--dpi <dpi>] [--ollama] [--ollama-model <model>] [--ollama-endpoint <url>]");
    Console.WriteLine("  epubfabric export <book.efproj> --format epub [--output <output.epub>]");
    Console.WriteLine();
    Console.WriteLine("  evaluate はEPUBを生成せず、ページ画像+検出ブロックと生成されるEPUB断片を左右対照したHTMLレポート（index.html）と定量メトリクス（metrics.json）を出力します。");
    Console.WriteLine("  --ollama を指定すると、Ollamaによる意味分類（見出し・本文などの補正）を行います（既定では無効）。");
    Console.WriteLine("  --ollama-model の既定値: gemma4:12b / --ollama-endpoint の既定値: http://localhost:11434");
}

sealed record OllamaOptions(bool Enabled, string Endpoint, string Model);
