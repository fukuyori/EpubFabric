using System.Text.Json;
using EpubFabric.Core.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace EpubFabric.Pipeline;

// The model pre/post-processing and recognition cascade are a C# adaptation of
// ndl-lab/ndlocr-lite v2.0.0 at ba8bb85 (originally ported from d25e0d4).
// NDLOCR-Lite: Copyright (c) 2023 National Diet Library, Japan, CC BY 4.0.
// This adaptation changes the runtime, image resizing, and error handling for
// EpubFabric, and returns the non-text block detections (figures, tables,
// running heads, folios) that the upstream CLI only writes to XML. The reading
// order follows the upstream recursive XY cut; see NdlOcrXyCutReadingOrder.

/// <summary>DEIMが検出した非文字行の領域。座標はページに対する0～1の比率。</summary>
public sealed record NdlOcrRegion(string ClassName, BoundingBox Bounds, float Confidence);

/// <summary>1ページ分の認識結果。</summary>
public sealed record NdlOcrPageResult(
    IReadOnlyList<TextLine> Lines,
    IReadOnlyList<NdlOcrRegion> Regions,
    int ImageWidth,
    int ImageHeight)
{
    public double AverageConfidence => Lines.Count == 0 ? 0 : Lines.Average(line => line.Confidence);

    public double VerticalLineShare => Lines.Count == 0
        ? 0
        : (double)Lines.Count(line => line.DetectedWritingMode == WritingMode.Vertical) / Lines.Count;
}

/// <summary>
/// NDLOCR-LiteのDEIM検出器とPARSeq認識器をONNX Runtimeで直接実行する。
/// モデルは配布物の <c>models\ndlocr</c> に同梱し、<c>--ndlocr-model-dir</c> で差し替えられる。
/// </summary>
public sealed class NativeNdlOcrService : IDisposable
{
    private const string CharsetFileName = "NDLmoji.yaml";
    private const float DetectorConfidenceThreshold = 0.25f;

    /// <summary>
    /// 公式版 <c>_suppress_overlapping_regions</c> の既定値。同じクラスで、より大きい領域に
    /// 自身の面積のこの割合以上が覆われる領域を捨てる。
    /// </summary>
    private const double OverlapSuppressionThreshold = 0.9;

    /// <summary>
    /// 公式版 <c>PARSEQ.preprocess</c> の回転判定。高さが幅の0.8倍を超える切り出しは
    /// 縦書きとして反時計回りに90度回してから認識する（v2.0.0で <c>h&gt;w</c> から変更）。
    /// </summary>
    private const double VerticalCropAspectRatio = 0.8;

    /// <summary>文字行ではないが、レイアウト解析へ渡す領域クラス。</summary>
    private static readonly HashSet<string> RegionClassNames = new(StringComparer.Ordinal)
    {
        "block_fig", "block_ad", "block_pillar", "block_folio", "block_chart",
        "block_eqn", "block_cfm", "block_table",
    };

    /// <summary>この領域の中心に含まれる文字行は、行クラスより領域の種別を優先する。</summary>
    private static readonly IReadOnlyDictionary<string, string> ContainerSourceTypeNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["block_pillar"] = "柱",
            ["block_folio"] = "ノンブル",
        };

    private static readonly string[] ClassNames =
    [
        "text_block", "line_main", "line_caption", "line_ad", "line_note", "line_note_tochu",
        "block_fig", "block_ad", "block_pillar", "block_folio", "block_rubi", "block_chart",
        "block_eqn", "block_cfm", "block_eng", "block_table", "line_title",
    ];

    private static readonly IReadOnlyDictionary<string, string> SourceTypeNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["line_main"] = "本文",
            ["line_caption"] = "キャプション",
            ["line_ad"] = "広告文字",
            ["line_note"] = "割注",
            ["line_note_tochu"] = "頭注",
            ["line_title"] = "タイトル本文",
        };

    private readonly NativeNdlOcrPipelineOptions options;
    private InferenceSession? detector;
    private Recognizer? recognizer30;
    private Recognizer? recognizer50;
    private Recognizer? recognizer100;

    public NativeNdlOcrService(NativeNdlOcrPipelineOptions options)
    {
        this.options = options;
    }

    /// <summary>
    /// モデルを読み込む。モデルがない・読めない場合は <see cref="ExternalBackendException"/> を投げる。
    /// パイプラインは最初のページでこれを呼び、失敗したら以降のページでNDLOCR-Liteを使わない。
    /// </summary>
    public void Initialize()
    {
        try
        {
            EnsureInitialized();
        }
        catch (ExternalBackendException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ExternalBackendException($"内蔵ONNX版NDLOCR-Liteを利用できません: {ex.Message}", ex);
        }
    }

    /// <summary>実際に使うモデルのディレクトリ。初期化前はnull。</summary>
    public string? ModelDirectory { get; private set; }

    public Task<NdlOcrPageResult> RecognizePageAsync(
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(RecognizePage(imagePath, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExternalBackendException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ExternalBackendException($"内蔵ONNX版NDLOCR-Liteを利用できません: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        recognizer100?.Dispose();
        recognizer50?.Dispose();
        recognizer30?.Dispose();
        detector?.Dispose();
    }

    private NdlOcrPageResult RecognizePage(string imagePath, CancellationToken cancellationToken)
    {
        EnsureInitialized();
        cancellationToken.ThrowIfCancellationRequested();
        using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (image.Empty())
        {
            throw new ExternalBackendException($"NDLOCR-Liteへ渡す画像を読めません: {imagePath}");
        }

        var allDetections = Detect(image);
        var regionDetections = allDetections
            .Where(detection => RegionClassNames.Contains(detection.ClassName))
            .ToList();
        var detections = allDetections
            .Where(detection => SourceTypeNames.ContainsKey(detection.ClassName))
            .ToList();
        var recognized = new List<RecognizedLine>(detections.Count);
        foreach (var detection in detections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = RecognizeCascade(image, detection);
            if (!string.IsNullOrWhiteSpace(text))
            {
                recognized.Add(new RecognizedLine(detection, text));
            }
        }

        if (recognized.Count == 0)
        {
            throw new ExternalBackendException("内蔵ONNX版NDLOCR-Liteが有効な文字行を検出できませんでした。");
        }

        var ordered = RemoveAdjacentDuplicates(AssignReadingOrder(recognized));
        var lines = ordered.Select((item, order) => new TextLine(
            ToPageBounds(item.Detection, image.Width, image.Height),
            item.Text,
            item.Detection.Confidence,
            TextSourceKind.NdlOcr,
            DetectedWritingMode: item.Detection.Height > item.Detection.Width
                ? WritingMode.Vertical
                : WritingMode.Horizontal,
            SourceReadingOrder: order,
            SourceType: ResolveSourceType(item.Detection, regionDetections))).ToList();

        var regions = regionDetections
            .Select(detection => new NdlOcrRegion(
                detection.ClassName,
                ToPageBounds(detection, image.Width, image.Height),
                detection.Confidence))
            .ToList();

        return new NdlOcrPageResult(lines, regions, image.Width, image.Height);
    }

    private static BoundingBox ToPageBounds(Detection detection, int width, int height) => new(
        detection.X1 / (double)width,
        detection.Y1 / (double)height,
        detection.Width / (double)width,
        detection.Height / (double)height);

    /// <summary>
    /// 行の中心が柱・ノンブル領域に入る場合はその種別を、そうでなければ行クラスの種別を返す。
    /// 公式版の <c>get_relationship_rect</c> と同じく中心点で包含を判定する。
    /// </summary>
    public static string ResolveSourceType(string lineClassName, int x1, int y1, int x2, int y2, IEnumerable<(string ClassName, int X1, int Y1, int X2, int Y2)> regions)
    {
        var centerX = (x1 + x2) / 2;
        var centerY = (y1 + y2) / 2;
        foreach (var region in regions)
        {
            if (ContainerSourceTypeNames.TryGetValue(region.ClassName, out var containerType)
                && centerX >= region.X1 && centerX <= region.X2
                && centerY >= region.Y1 && centerY <= region.Y2)
            {
                return containerType;
            }
        }

        return SourceTypeNames[lineClassName];
    }

    private static string ResolveSourceType(Detection line, IReadOnlyList<Detection> regions) =>
        ResolveSourceType(
            line.ClassName,
            line.X1,
            line.Y1,
            line.X2,
            line.Y2,
            regions.Select(region => (region.ClassName, region.X1, region.Y1, region.X2, region.Y2)));

    private void EnsureInitialized()
    {
        if (detector is not null)
        {
            return;
        }

        var directory = options.ModelDirectory
            ?? NdlOcrModelPaths.FindBundledDirectory(AppContext.BaseDirectory)
            ?? throw new ExternalBackendException(
                "同梱のNDLOCR-Liteモデル（models\\ndlocr）が見つかりません。--ndlocr-model-dir で指定できます。");
        var paths = NdlOcrModelPaths.Resolve(directory);
        using var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        InferenceSession? newDetector = null;
        Recognizer? newRecognizer30 = null;
        Recognizer? newRecognizer50 = null;
        Recognizer? newRecognizer100 = null;
        try
        {
            newDetector = new InferenceSession(paths.DetectorPath, sessionOptions);
            var charset = LoadCharset(paths.CharsetPath);
            newRecognizer30 = new Recognizer(paths.Recognizer30Path, charset);
            newRecognizer50 = new Recognizer(paths.Recognizer50Path, charset);
            newRecognizer100 = new Recognizer(paths.Recognizer100Path, charset);

            detector = newDetector;
            ModelDirectory = Path.GetDirectoryName(paths.DetectorPath);
            recognizer30 = newRecognizer30;
            recognizer50 = newRecognizer50;
            recognizer100 = newRecognizer100;
        }
        catch
        {
            newRecognizer100?.Dispose();
            newRecognizer50?.Dispose();
            newRecognizer30?.Dispose();
            newDetector?.Dispose();
            throw;
        }
    }

    private List<Detection> Detect(Mat image)
    {
        var side = Math.Max(image.Width, image.Height);
        using var padded = Mat.Zeros(side, side, MatType.CV_8UC3).ToMat();
        using (var destination = new Mat(padded, new Rect(0, 0, image.Width, image.Height)))
        {
            image.CopyTo(destination);
        }

        using var resized = new Mat();
        // NDLOCR-LiteのPillow縮小と地理サンプルで同じ検出座標になる縮小方式。
        Cv2.Resize(padded, resized, new Size(800, 800), interpolation: InterpolationFlags.Area);
        var input = ToDetectorTensor(resized);
        var targetSize = new DenseTensor<long>(new[] { 1, 2 });
        targetSize[0, 0] = 800;
        targetSize[0, 1] = 800;

        using var results = detector!.Run(
        [
            NamedOnnxValue.CreateFromTensor("images", input),
            NamedOnnxValue.CreateFromTensor("orig_target_sizes", targetSize),
        ]);
        var labels = results.First(result => result.Name == "labels").AsTensor<long>().ToArray();
        var boxes = results.First(result => result.Name == "boxes").AsTensor<float>().ToArray();
        var scores = results.First(result => result.Name == "scores").AsTensor<float>().ToArray();
        var charCounts = results.First(result => result.Name == "char_count").AsTensor<long>().ToArray();

        var scale = side / 800f;
        var detections = new List<Detection>();
        for (var i = 0; i < scores.Length; i++)
        {
            if (scores[i] <= DetectorConfidenceThreshold)
            {
                continue;
            }

            var classIndex = checked((int)labels[i] - 1);
            if ((uint)classIndex >= ClassNames.Length)
            {
                continue;
            }

            var className = ClassNames[classIndex];
            if (!SourceTypeNames.ContainsKey(className) && !RegionClassNames.Contains(className))
            {
                continue;
            }

            var x1 = Math.Clamp((int)(boxes[(i * 4) + 0] * scale), 0, image.Width);
            var y1 = Math.Clamp((int)(boxes[(i * 4) + 1] * scale), 0, image.Height);
            var x2 = Math.Clamp((int)(boxes[(i * 4) + 2] * scale), 0, image.Width);
            var y2 = Math.Clamp((int)(boxes[(i * 4) + 3] * scale), 0, image.Height);
            if (x2 <= x1 || y2 <= y1)
            {
                continue;
            }

            detections.Add(new Detection(
                className,
                x1,
                y1,
                x2,
                y2,
                scores[i],
                checked((int)charCounts[i])));
        }

        var keep = SuppressOverlappingRegions(detections
            .Select(item => new DetectionBox(item.ClassName, item.X1, item.Y1, item.X2, item.Y2))
            .ToList());
        return keep.Select(index => detections[index]).ToList();
    }

    private string RecognizeCascade(Mat image, Detection detection)
    {
        using var crop = new Mat(image, new Rect(detection.X1, detection.Y1, detection.Width, detection.Height)).Clone();
        if (detection.PredictedCharacterCount == 3)
        {
            var text = recognizer30!.Read(crop);
            return text.EnumerateRunes().Count() >= 25 ? recognizer50!.Read(crop) : text;
        }

        if (detection.PredictedCharacterCount == 2)
        {
            var text = recognizer50!.Read(crop);
            return text.EnumerateRunes().Count() >= 45 ? recognizer100!.Read(crop) : text;
        }

        var result = recognizer100!.Read(crop);
        if (result.EnumerateRunes().Count() < 98 || crop.Height >= crop.Width)
        {
            return result;
        }

        var halfWidth = crop.Width / 2;
        using var left = new Mat(crop, new Rect(0, 0, halfWidth, crop.Height));
        using var right = new Mat(crop, new Rect(halfWidth, 0, crop.Width - halfWidth, crop.Height));
        return recognizer100.Read(left) + recognizer100.Read(right);
    }

    private static DenseTensor<float> ToDetectorTensor(Mat resized)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, 800, 800 });
        var mean = new[] { 0.485f, 0.456f, 0.406f };
        var std = new[] { 0.229f, 0.224f, 0.225f };
        for (var y = 0; y < 800; y++)
        {
            for (var x = 0; x < 800; x++)
            {
                var pixel = resized.At<Vec3b>(y, x);
                tensor[0, 0, y, x] = ((pixel.Item2 / 255f) - mean[0]) / std[0];
                tensor[0, 1, y, x] = ((pixel.Item1 / 255f) - mean[1]) / std[1];
                tensor[0, 2, y, x] = ((pixel.Item0 / 255f) - mean[2]) / std[2];
            }
        }

        return tensor;
    }

    /// <summary>
    /// 公式版と同じ再帰XY切断で読み順を決める。独自の帯分割では、上下に分かれた
    /// 記事帯をまたぐ列が本文の途中へ割り込む誤りが出たため、
    /// <see cref="NdlOcrXyCutReadingOrder"/>へ置き換えた。
    /// </summary>
    private static IReadOnlyList<RecognizedLine> AssignReadingOrder(IReadOnlyList<RecognizedLine> lines)
    {
        var boxes = lines
            .Select(line => new NdlOcrXyCutReadingOrder.LineBox(
                line.Detection.X1,
                line.Detection.Y1,
                line.Detection.X2,
                line.Detection.Y2))
            .ToList();

        var ranks = NdlOcrXyCutReadingOrder.Solve(boxes);
        return lines
            .Select((line, index) => (Line: line, Rank: ranks[index], Index: index))
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Index)
            .Select(item => item.Line)
            .ToList();
    }

    /// <summary>
    /// 読み順に並べたあと、直前の行とほぼ同じ領域を占める行を落とす。
    /// 検出段階のIoU判定では入れ子の重複を取り逃がすため、公式版の<c>remove_dup</c>相当を加える。
    /// </summary>
    private static IReadOnlyList<RecognizedLine> RemoveAdjacentDuplicates(IReadOnlyList<RecognizedLine> ordered)
    {
        var candidates = ordered
            .Select(line => new NdlOcrAdjacentDuplicateFilter.LineCandidate(
                line.Detection.X1,
                line.Detection.Y1,
                line.Detection.X2,
                line.Detection.Y2,
                line.Detection.Confidence))
            .ToList();

        var kept = NdlOcrAdjacentDuplicateFilter.KeepIndices(candidates);
        return kept.Count == ordered.Count
            ? ordered
            : kept.Select(index => ordered[index]).ToList();
    }

    /// <summary>重なり抑制の入力。座標はページ画像の画素単位。</summary>
    public readonly record struct DetectionBox(string ClassName, int X1, int Y1, int X2, int Y2)
    {
        public long Area => (long)Math.Max(0, X2 - X1) * Math.Max(0, Y2 - Y1);
    }

    /// <summary>
    /// 公式版 v2.0.0 の <c>_suppress_overlapping_regions</c>。同じクラスの中で面積の大きい順に
    /// 調べ、すでに残した自分より大きい領域に自身の面積の90%以上が覆われる領域を捨てる。
    /// 戻り値は残す検出の添字で、入力の順序を保つ。
    /// </summary>
    public static IReadOnlyList<int> SuppressOverlappingRegions(IReadOnlyList<DetectionBox> boxes)
    {
        var keptByClass = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var suppressed = new HashSet<int>();

        // 公式版はPythonの安定ソート（sorted, reverse=True）なので、同じ面積は入力順を保つ。
        var byArea = Enumerable.Range(0, boxes.Count)
            .OrderByDescending(index => boxes[index].Area)
            .ToList();
        foreach (var index in byArea)
        {
            var box = boxes[index];
            if (box.Area <= 0)
            {
                // 公式版は面積0の検出を残す側にも抑制する側にも入れない（出力には残る）。
                continue;
            }

            if (!keptByClass.TryGetValue(box.ClassName, out var kept))
            {
                kept = [];
                keptByClass[box.ClassName] = kept;
            }

            var isSuppressed = false;
            foreach (var otherIndex in kept)
            {
                var other = boxes[otherIndex];
                if (other.Area <= box.Area)
                {
                    continue;
                }

                var intersection = (long)Math.Max(0, Math.Min(box.X2, other.X2) - Math.Max(box.X1, other.X1))
                    * Math.Max(0, Math.Min(box.Y2, other.Y2) - Math.Max(box.Y1, other.Y1));
                if ((double)intersection / box.Area >= OverlapSuppressionThreshold)
                {
                    isSuppressed = true;
                    break;
                }
            }

            if (isSuppressed)
            {
                suppressed.Add(index);
            }
            else
            {
                kept.Add(index);
            }
        }

        return Enumerable.Range(0, boxes.Count).Where(index => !suppressed.Contains(index)).ToList();
    }

    /// <summary>公式版 v2.0.0 の <c>_remove_ascii_spaces_around_japanese_punctuation</c>。</summary>
    public static string RemoveSpacesAroundJapanesePunctuation(string text)
    {
        foreach (var punctuation in "、。")
        {
            text = text.Replace($" {punctuation}", punctuation.ToString(), StringComparison.Ordinal);
            text = text.Replace($"{punctuation} ", punctuation.ToString(), StringComparison.Ordinal);
        }

        return text;
    }

    private static string LoadCharset(string path)
    {
        const string key = "charset_train:";
        var line = File.ReadLines(path).FirstOrDefault(value => value.TrimStart().StartsWith(key, StringComparison.Ordinal));
        if (line is null)
        {
            throw new InvalidDataException($"{CharsetFileName}にcharset_trainがありません。");
        }

        var encoded = line[(line.IndexOf(key, StringComparison.Ordinal) + key.Length)..].Trim();
        var charset = JsonSerializer.Deserialize<string>(encoded);
        if (string.IsNullOrEmpty(charset))
        {
            throw new InvalidDataException($"{CharsetFileName}のcharset_trainを読めません。");
        }

        return charset;
    }

    private sealed record Detection(
        string ClassName,
        int X1,
        int Y1,
        int X2,
        int Y2,
        float Confidence,
        int PredictedCharacterCount)
    {
        public int Width => X2 - X1;
        public int Height => Y2 - Y1;
    }

    private sealed record RecognizedLine(Detection Detection, string Text);

    private sealed class Recognizer : IDisposable
    {
        private readonly InferenceSession session;
        private readonly string[] charset;
        private readonly int width;
        private readonly int tokenCount;

        public Recognizer(string modelPath, string charset)
        {
            using var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = 1,
                InterOpNumThreads = 1,
            };
            session = new InferenceSession(modelPath, options);
            this.charset = charset.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
            var dimensions = session.InputMetadata["images"].Dimensions;
            width = dimensions[3];
            tokenCount = session.OutputMetadata.Values.Single().Dimensions[1];
        }

        public string Read(Mat source)
        {
            var rotate = source.Height > source.Width * VerticalCropAspectRatio;
            using var oriented = rotate ? new Mat() : source.Clone();
            if (rotate)
            {
                Cv2.Rotate(source, oriented, RotateFlags.Rotate90Counterclockwise);
            }

            using var resized = new Mat();
            Cv2.Resize(oriented, resized, new Size(width, 24), interpolation: InterpolationFlags.Linear);
            var tensor = new DenseTensor<float>(new[] { 1, 3, 24, width });
            for (var y = 0; y < 24; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var pixel = resized.At<Vec3b>(y, x);
                    tensor[0, 0, y, x] = (pixel.Item0 / 127.5f) - 1f;
                    tensor[0, 1, y, x] = (pixel.Item1 / 127.5f) - 1f;
                    tensor[0, 2, y, x] = (pixel.Item2 / 127.5f) - 1f;
                }
            }

            using var results = session.Run([NamedOnnxValue.CreateFromTensor("images", tensor)]);
            var logits = results.Single().AsTensor<float>().ToArray();
            var classCount = logits.Length / tokenCount;
            var text = new System.Text.StringBuilder();
            for (var token = 0; token < tokenCount; token++)
            {
                var offset = token * classCount;
                var bestIndex = 0;
                var bestScore = logits[offset];
                for (var character = 1; character < classCount; character++)
                {
                    if (logits[offset + character] > bestScore)
                    {
                        bestScore = logits[offset + character];
                        bestIndex = character;
                    }
                }

                if (bestIndex == 0)
                {
                    break;
                }

                var charsetIndex = bestIndex - 1;
                if ((uint)charsetIndex < charset.Length)
                {
                    text.Append(charset[charsetIndex]);
                }
            }

            return RemoveSpacesAroundJapanesePunctuation(text.ToString());
        }

        public void Dispose() => session.Dispose();
    }
}

public sealed record NdlOcrModelPaths(
    string DetectorPath,
    string Recognizer30Path,
    string Recognizer50Path,
    string Recognizer100Path,
    string CharsetPath)
{
    public static NdlOcrModelPaths Resolve(string directory)
    {
        var root = Path.GetFullPath(directory);
        var modelDirectory = File.Exists(Path.Combine(root, DetectorFileName))
            ? root
            : Path.Combine(root, "src", "model");
        var configDirectory = File.Exists(Path.Combine(root, CharsetFileName))
            ? root
            : Path.Combine(root, "src", "config");
        var paths = new NdlOcrModelPaths(
            Path.Combine(modelDirectory, DetectorFileName),
            Path.Combine(modelDirectory, Recognizer30FileName),
            Path.Combine(modelDirectory, Recognizer50FileName),
            Path.Combine(modelDirectory, Recognizer100FileName),
            Path.Combine(configDirectory, CharsetFileName));
        var missing = paths.AllPaths().Where(path => !File.Exists(path)).ToList();
        if (missing.Count > 0)
        {
            throw new DirectoryNotFoundException(
                $"NDLOCR-Liteのモデル構成が不足しています: {string.Join(", ", missing.Select(Path.GetFileName))}");
        }

        return paths;
    }

    private IEnumerable<string> AllPaths()
    {
        yield return DetectorPath;
        yield return Recognizer30Path;
        yield return Recognizer50Path;
        yield return Recognizer100Path;
        yield return CharsetPath;
    }

    /// <summary>
    /// 既定のモデル配置先を探す。インストーラーは <c>{app}\models\ndlocr</c> へ置き、
    /// GUIは <c>{app}</c>、CLIは <c>{app}\cli</c> から起動するため、実行ファイルの隣と
    /// 1つ上の両方を調べる。見つからなければ null を返す。
    /// </summary>
    public static string? FindBundledDirectory(string baseDirectory)
    {
        foreach (var candidate in new[]
        {
            Path.Combine(baseDirectory, "models", "ndlocr"),
            Path.Combine(baseDirectory, "..", "models", "ndlocr"),
        })
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(Path.Combine(fullPath, DetectorFileName)))
            {
                return fullPath;
            }
        }

        return null;
    }

    /// <summary>同梱・指定ディレクトリに必要なファイル名（配布スクリプトの検証にも使う）。</summary>
    public static IReadOnlyList<string> RequiredFileNames { get; } =
    [
        DetectorFileName,
        Recognizer30FileName,
        Recognizer50FileName,
        Recognizer100FileName,
        CharsetFileName,
    ];

    // ndl-lab/ndlocr-lite v2.0.0 (ba8bb85) の src/model と src/config に含まれるファイル名。
    private const string DetectorFileName = "deim-s-1024x1024-261124.onnx";
    private const string Recognizer30FileName = "parseq-ndl-24x256-30-tiny-499epoch-261124.onnx";
    private const string Recognizer50FileName = "parseq-ndl-24x384-50-tiny-278epoch-261124.onnx";
    private const string Recognizer100FileName = "parseq-ndl-24x768-100-tiny-153epoch-261124.onnx";
    private const string CharsetFileName = "NDLmoji.yaml";
}
