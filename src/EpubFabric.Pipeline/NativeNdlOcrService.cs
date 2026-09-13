using System.Text.Json;
using EpubFabric.Core.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace EpubFabric.Pipeline;

// The model pre/post-processing and recognition cascade are a C# adaptation of
// ndl-lab/ndlocr-lite at d25e0d415b607ad44459ca6b95c7512a54363935.
// NDLOCR-Lite: Copyright (c) 2023 National Diet Library, Japan, CC BY 4.0.
// This adaptation changes the runtime, image resizing, duplicate suppression,
// and error handling for EpubFabric. The reading order follows the upstream
// recursive XY cut; see NdlOcrXyCutReadingOrder.

/// <summary>
/// NDLOCR-LiteのDEIM検出器とPARSeq認識器をONNX Runtimeで直接実行する。
/// モデルの再配布方法を確定するまでは、公式配布物のディレクトリを明示指定して使用する。
/// </summary>
public sealed class NativeNdlOcrService : INdlOcrPageService
{
    private const string CharsetFileName = "NDLmoji.yaml";
    private const float DetectorConfidenceThreshold = 0.25f;

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

    public Task<NdlOcrPageResult> RecognizePageAsync(
        string imagePath,
        string outputDirectory,
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

        var detections = DetectLines(image);
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

        var ordered = AssignReadingOrder(recognized);
        var lines = ordered.Select((item, order) => new TextLine(
            new BoundingBox(
                item.Detection.X1 / (double)image.Width,
                item.Detection.Y1 / (double)image.Height,
                (item.Detection.X2 - item.Detection.X1) / (double)image.Width,
                (item.Detection.Y2 - item.Detection.Y1) / (double)image.Height),
            item.Text,
            item.Detection.Confidence,
            TextSourceKind.NdlOcr,
            DetectedWritingMode: item.Detection.Height > item.Detection.Width
                ? WritingMode.Vertical
                : WritingMode.Horizontal,
            SourceReadingOrder: order,
            SourceType: SourceTypeNames[item.Detection.ClassName])).ToList();

        return new NdlOcrPageResult(lines, image.Width, image.Height);
    }

    private void EnsureInitialized()
    {
        if (detector is not null)
        {
            return;
        }

        var paths = NdlOcrModelPaths.Resolve(options.ModelDirectory);
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

    private List<Detection> DetectLines(Mat image)
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
            if (!SourceTypeNames.ContainsKey(className))
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

        return RemoveDuplicateDetections(detections);
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

    private static List<Detection> RemoveDuplicateDetections(IReadOnlyList<Detection> detections)
    {
        var kept = new List<Detection>();
        foreach (var detection in detections.OrderByDescending(item => item.Confidence))
        {
            if (kept.Any(item => IntersectionOverUnion(item, detection) >= 0.90))
            {
                continue;
            }

            kept.Add(detection);
        }

        return kept;
    }

    private static double IntersectionOverUnion(Detection left, Detection right)
    {
        var intersectionWidth = Math.Max(0, Math.Min(left.X2, right.X2) - Math.Max(left.X1, right.X1));
        var intersectionHeight = Math.Max(0, Math.Min(left.Y2, right.Y2) - Math.Max(left.Y1, right.Y1));
        var intersection = intersectionWidth * intersectionHeight;
        var union = (left.Width * left.Height) + (right.Width * right.Height) - intersection;
        return union <= 0 ? 0 : intersection / (double)union;
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
            using var oriented = source.Height > source.Width ? new Mat() : source.Clone();
            if (source.Height > source.Width)
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

            return text.ToString();
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

    private const string DetectorFileName = "deim-s-1024x1024.onnx";
    private const string Recognizer30FileName = "parseq-ndl-24x256-30-tiny-189epoch-tegaki3-r8data-202604.onnx";
    private const string Recognizer50FileName = "parseq-ndl-24x384-50-tiny-300epoch-tegaki3-r8data-202604.onnx";
    private const string Recognizer100FileName = "parseq-ndl-24x768-100-tiny-153epoch-tegaki3-r8data-202604.onnx";
    private const string CharsetFileName = "NDLmoji.yaml";
}
