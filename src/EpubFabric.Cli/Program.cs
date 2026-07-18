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
using EpubFabric.Pipeline;

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
    if (!TryParseLayout(options, out var layout))
    {
        return 1;
    }

    var workDirectory = Path.Combine(Path.GetTempPath(), $"epubfabric-{Guid.NewGuid():N}");
    var (project, _) = await BuildProjectFromPdf(
        inputPath,
        workDirectory,
        dpi,
        ollamaOptions,
        preserveAllTextLines: layout == OutputLayout.Fixed,
        enhancePages: options.ContainsKey("--enhance"));

    BuildEpub(project, layout, outputPath, ParsePageImageOptions(options));

    Console.WriteLine($"{LayoutLabel(layout)}EPUBを生成しました: {outputPath}");
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
    var (project, pages) = await BuildProjectFromPdf(
        inputPath,
        workDirectory,
        dpi,
        ollamaOptions,
        preserveAllTextLines: false,
        enhancePages: options.ContainsKey("--enhance"));

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
    var (project, _) = await BuildProjectFromPdf(
        inputPath,
        workDirectory,
        dpi,
        ollamaOptions,
        preserveAllTextLines: true,
        enhancePages: options.ContainsKey("--enhance"));

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

    if (!TryParseLayout(options, out var layout))
    {
        return 1;
    }

    var project = new EfprojStore().Load(projectDirectory);
    var blocksById = project.Pages.SelectMany(p => p.Blocks).ToDictionary(b => b.Id);

    var correctedCount = blocksById.Values.Count(b => b.IsManuallyEdited);
    if (correctedCount > 0)
    {
        Console.WriteLine($"{correctedCount} 件の校正済みブロックを反映します。");
    }

    BuildEpub(project, layout, outputPath, ParsePageImageOptions(options));

    Console.WriteLine($"{LayoutLabel(layout)}EPUBを生成しました: {outputPath}");
    return 0;
}

static void BuildEpub(EpubFabricProject project, OutputLayout layout, string outputPath, PageImageEncodingOptions? imageOptions = null) =>
    new ConversionPipeline().BuildEpub(project, layout, outputPath, imageOptions);

static bool TryParseLayout(Dictionary<string, string> options, out OutputLayout layout)
{
    var value = options.GetValueOrDefault("--layout", "fixed");
    if (string.Equals(value, "fixed", StringComparison.OrdinalIgnoreCase))
    {
        layout = OutputLayout.Fixed;
        return true;
    }

    if (string.Equals(value, "reflow", StringComparison.OrdinalIgnoreCase))
    {
        layout = OutputLayout.Reflow;
        return true;
    }

    layout = default;
    Console.Error.WriteLine($"エラー: --layout は fixed または reflow を指定してください（指定値: {value}）。");
    return false;
}

static string LayoutLabel(OutputLayout layout) =>
    layout == OutputLayout.Fixed ? "固定レイアウト" : "リフロー型";

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

// 変換パイプライン本体はEpubFabric.Pipeline（GUIと共有）。CLIは進捗をコンソールへ流すだけ。
static async Task<(EpubFabricProject Project, List<DocumentPage> Pages)> BuildProjectFromPdf(
    string inputPath,
    string workDirectory,
    int dpi,
    OllamaOptions? ollamaOptions = null,
    bool preserveAllTextLines = true,
    bool enhancePages = false)
{
    var options = new ConversionOptions
    {
        InputPath = inputPath,
        WorkDirectory = workDirectory,
        Dpi = dpi,
        PreserveAllTextLines = preserveAllTextLines,
        EnhancePages = enhancePages,
        Ollama = ollamaOptions is { Enabled: true }
            ? new OllamaPipelineOptions(ollamaOptions.Endpoint, ollamaOptions.Model)
            : null,
    };

    return await new ConversionPipeline().BuildProjectAsync(options, new ConsoleProgress());
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

// 固定レイアウトのページ画像収録設定（12章）。既定はJPEG品質85・長辺2200px。
// --max-image-size 0 で無制限（再圧縮もしない従来動作に近い挙動）を選べる。
static PageImageEncodingOptions ParsePageImageOptions(Dictionary<string, string> options)
{
    var quality = 85;
    if (options.TryGetValue("--image-quality", out var qualityValue) && int.TryParse(qualityValue, out var parsedQuality))
    {
        quality = Math.Clamp(parsedQuality, 1, 100);
    }

    var maxSide = 2200;
    if (options.TryGetValue("--max-image-size", out var sizeValue) && int.TryParse(sizeValue, out var parsedSize))
    {
        maxSide = parsedSize;
    }

    return new PageImageEncodingOptions(quality, maxSide);
}

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
    Console.WriteLine("  epubfabric convert <input.pdf> [--output <output.epub>] [--layout <fixed|reflow>] [--dpi <dpi>] [--enhance] [--image-quality <1-100>] [--max-image-size <px>] [--ollama] [--ollama-model <model>] [--ollama-endpoint <url>]");
    Console.WriteLine("  epubfabric evaluate <input.pdf> [--report <report-dir>] [--dpi <dpi>] [--ollama] [--ollama-model <model>] [--ollama-endpoint <url>]");
    Console.WriteLine("  epubfabric analyze <input.pdf> --project <book.efproj> [--dpi <dpi>] [--ollama] [--ollama-model <model>] [--ollama-endpoint <url>]");
    Console.WriteLine("  epubfabric export <book.efproj> --format epub [--output <output.epub>] [--layout <fixed|reflow>] [--image-quality <1-100>] [--max-image-size <px>]");
    Console.WriteLine();
    Console.WriteLine("  convert/export は固定レイアウトEPUBを生成します。従来のリフロー型は --layout reflow で選択できます。");
    Console.WriteLine("  evaluate はEPUBを生成せず、ページ画像+検出ブロックと生成されるEPUB断片を左右対照したHTMLレポート（index.html）と定量メトリクス（metrics.json）を出力します。");
    Console.WriteLine("  固定レイアウトのページ画像はJPEG品質85・長辺2200pxへ再圧縮して収録します（--image-quality / --max-image-size で変更、--max-image-size 0 で縮小なし）。");
    Console.WriteLine("  --enhance を指定すると、スキャン紙面を高品質化（紙色の白色正規化・コントラスト補正・裏写り抑制）してEPUB収録・OCRに使います。");
    Console.WriteLine("  --ollama を指定すると、Ollamaによる意味分類（見出し・本文などの補正）とOCR文字列の校正を行います（既定では無効）。");
    Console.WriteLine("  --ollama-model の既定値: gemma4:12b / --ollama-endpoint の既定値: http://localhost:11434");
}

sealed record OllamaOptions(bool Enabled, string Endpoint, string Model);

/// <summary>パイプラインの進捗メッセージを、従来どおりの形式でコンソールへ流す。</summary>
sealed class ConsoleProgress : IProgress<ConversionProgress>
{
    public void Report(ConversionProgress value) => Console.WriteLine(value.Message);
}
