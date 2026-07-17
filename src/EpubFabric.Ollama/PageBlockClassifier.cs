using System.Text;
using System.Text.Json;
using EpubFabric.Core.Models;

namespace EpubFabric.Ollama;

/// <summary>
/// 9.7 Ollamaによる意味分類：ルールベース（HeuristicLayoutAnalyzer）が仮に割り当てた
/// ブロック種別・見出しレベルを、OCR文字列の文脈から意味的に検証・補正する。
/// 実データ検証の結果、ページ画像全体をこの解像度でローカル視覚モデルに渡しても
/// 色や図の判別が信頼できないことが分かったため（例：黄色のハイライトボックスを
/// 「薄いグレー」と誤認識）、この初期実装では画像を渡さずOCR文字列の文脈のみで
/// 判定する。見出しかどうかの判定は本来テキストの意味理解の問題であり、視覚情報が
/// 無くても十分に有効である（表紙の飾り文字と記事タイトルの混同、著者名の見出し誤判定
/// など、ルールベースの既知の弱点はどちらも文脈から判断できる）。
/// 応答は13.3節のJSON Schemaで構造化させ、13.4節の検証を経て初めて適用する。
/// 文章の要約・加筆・創作はさせず、分類（種別・見出しレベル）のみを行わせる。
/// </summary>
public sealed class PageBlockClassifier
{
    private static readonly (string Key, BlockType Type)[] TypeMap =
    [
        ("chapter_title", BlockType.ChapterTitle),
        ("section_heading", BlockType.SectionHeading),
        ("subheading", BlockType.Subheading),
        ("body", BlockType.Body),
        ("aside", BlockType.Aside),
        ("pull_quote", BlockType.PullQuote),
        ("caption", BlockType.Caption),
        ("footnote", BlockType.Footnote),
        ("decorative", BlockType.Decorative),
        ("header", BlockType.Header),
        ("footer", BlockType.Footer),
        ("page_number", BlockType.PageNumber),
    ];

    private static readonly JsonElement ResponseSchema = BuildSchema();

    private readonly OllamaClient _client;
    private readonly string _model;
    private readonly TimeSpan _timeout;
    private readonly int _maxRetryCount;

    public PageBlockClassifier(OllamaClient client, string model, TimeSpan? timeout = null, int maxRetryCount = 2)
    {
        _client = client;
        _model = model;
        _timeout = timeout ?? TimeSpan.FromSeconds(120);
        _maxRetryCount = maxRetryCount;
    }

    /// <summary>
    /// ページ内のブロックを分類し、検証に成功した項目だけをその場で更新する。
    /// 戻り値は実際に分類が変化したブロック数。
    /// </summary>
    public async Task<int> ClassifyPageAsync(DocumentPage page, CancellationToken cancellationToken = default)
    {
        var candidates = page.Blocks
            .Where(b => !b.IsExcluded && b.Type is not (BlockType.Figure or BlockType.Table))
            .OrderBy(b => b.ReadingOrder)
            .ToList();

        if (candidates.Count == 0)
        {
            return 0;
        }

        var prompt = BuildPrompt(page, candidates);

        Exception? lastError = null;
        for (var attempt = 0; attempt <= _maxRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string raw;
            try
            {
                raw = await _client.GenerateAsync(_model, prompt, ResponseSchema, _timeout, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                lastError = ex;
                continue;
            }

            var validated = Validate(raw, candidates);
            if (validated is null)
            {
                lastError = new OllamaClassificationException($"Ollamaの応答を検証できませんでした: {raw}");
                continue;
            }

            return Apply(candidates, validated);
        }

        throw new OllamaClassificationException(
            $"Ollamaによる分類に{_maxRetryCount + 1}回失敗しました。ルールベースの結果をそのまま使用してください。",
            lastError ?? new InvalidOperationException("unknown error"));
    }

    private static int Apply(IReadOnlyList<PageBlock> candidates, IReadOnlyList<BlockClassification> results)
    {
        var byId = candidates.ToDictionary(b => b.Id);
        var changedCount = 0;

        foreach (var result in results)
        {
            var block = byId[result.Id];
            var changed = block.Type != result.Type || block.HeadingLevel != result.HeadingLevel;
            var wasExcluded = block.IsExcluded;

            block.Type = result.Type;
            block.HeadingLevel = result.HeadingLevel;
            block.ClassificationConfidence = result.Confidence;

            var suggestsExclusion = result.Type is BlockType.Header or BlockType.Footer or BlockType.PageNumber;

            // 9.9節「EPUBへの収録有無」は人手校正の対象と定義されているため、Ollamaの判定だけで
            // 本文ブロックを新たに除外（＝EPUBから消える）ことはしない。既に除外済みの分類を
            // 追認する場合はそのまま反映するが、新たに除外を提案する場合は要確認フラグを立てる
            // にとどめ、実際の除外は人手校正に委ねる。
            if (suggestsExclusion && !wasExcluded)
            {
                block.RequiresReview = true;
            }
            else
            {
                block.IsExcluded = suggestsExclusion;
            }

            if (changed)
            {
                changedCount++;
            }
        }

        return changedCount;
    }

    private static string BuildPrompt(DocumentPage page, IReadOnlyList<PageBlock> blocks)
    {
        var writingModeText = page.WritingMode == WritingMode.Vertical ? "縦書き" : "横書き";

        var sb = new StringBuilder();
        sb.AppendLine("あなたは、紙面をスキャンしOCRした電子書籍制作システムの一部として、");
        sb.AppendLine("ページ内の文章ブロックを分類するアシスタントです。");
        sb.AppendLine();
        sb.AppendLine($"これは{page.PageNumber}ページ目（{writingModeText}）から抽出したブロックの一覧です。");
        sb.AppendLine("各ブロックはルールベースの文字サイズ推定による仮の分類が付いていますが、誤りを含みます。");
        sb.AppendLine("ブロックは読み順に並んでいます。それぞれについて、文脈から種別と見出しレベルを判定してください。");
        sb.AppendLine();
        sb.AppendLine("種別は次のいずれかを選んでください:");
        sb.AppendLine("- chapter_title: 章タイトル・記事の大見出し");
        sb.AppendLine("- section_heading: 節見出し");
        sb.AppendLine("- subheading: 小見出し");
        sb.AppendLine("- body: 本文");
        sb.AppendLine("- aside: 囲み記事");
        sb.AppendLine("- pull_quote: 引用・強調文");
        sb.AppendLine("- caption: 図表のキャプション");
        sb.AppendLine("- footnote: 脚注");
        sb.AppendLine("- decorative: ロゴ・装飾的な文字列（本文として意味を持たない）");
        sb.AppendLine("- header: 柱（ページ上部の繰り返し表示）");
        sb.AppendLine("- footer: フッター（ページ下部の繰り返し表示、雑誌名や号数など）");
        sb.AppendLine("- page_number: ノンブル（ページ番号）");
        sb.AppendLine();
        sb.AppendLine("注意:");
        sb.AppendLine("- 文字が大きいだけで見出しとは限りません。著者名・日付・雑誌名・装飾的なロゴ文字は見出しではありません。");
        sb.AppendLine("- 見出しは通常、記事や章の内容を要約する短い句です。");
        sb.AppendLine("- 各ブロックのテキストを変更したり、要約や新しい文章を作成したりしないでください。分類のみを行ってください。");
        sb.AppendLine("- headingLevelは見出しの場合のみ1以上（1が最上位）とし、見出しでない場合は0にしてください。");
        sb.AppendLine();
        sb.AppendLine("ブロック一覧:");

        foreach (var block in blocks)
        {
            var text = (block.CorrectedText ?? block.OcrText).Replace('\n', ' ');
            if (text.Length > 80)
            {
                text = text[..80] + "…";
            }

            sb.AppendLine($"- id: {block.Id}, 現在の分類: {ToKey(block.Type)}, テキスト: \"{text}\"");
        }

        return sb.ToString();
    }

    private static List<BlockClassification>? Validate(string raw, IReadOnlyList<PageBlock> candidates)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("blocks", out var blocksElement) || blocksElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var validIds = candidates.Select(b => b.Id).ToHashSet();
            var seenIds = new HashSet<string>();
            var results = new List<BlockClassification>();

            // ローカルLLMは、IDの一部欠落・範囲外の見出しレベル（実データでは1000000等が
            // 返ってきた）・重複IDなど、項目単位の軽微な誤りをまれに起こす。1件の不備で
            // 応答全体（他の正しい分類結果を含む）を捨てるのはもったいないため、不正な
            // 項目はその1件だけ読み捨てて処理を続ける。JSONとして解析できない場合や
            // "blocks"配列そのものが無い場合のみ応答全体を不正とする（13.4節）。
            foreach (var item in blocksElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = idElement.GetString()!;
                if (!validIds.Contains(id) || !seenIds.Add(id))
                {
                    continue;
                }

                if (!item.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String
                    || !TryMapType(typeElement.GetString()!, out var type))
                {
                    continue;
                }

                var headingLevel = 0;
                if (item.TryGetProperty("headingLevel", out var headingLevelElement)
                    && headingLevelElement.ValueKind == JsonValueKind.Number
                    && headingLevelElement.TryGetInt32(out var parsedHeadingLevel))
                {
                    headingLevel = parsedHeadingLevel;
                }

                if (headingLevel is < 0 or > 6)
                {
                    continue;
                }

                var confidence = 0.0;
                if (item.TryGetProperty("confidence", out var confidenceElement) && confidenceElement.ValueKind == JsonValueKind.Number)
                {
                    confidence = confidenceElement.GetDouble();
                }

                if (confidence is < 0 or > 1)
                {
                    continue;
                }

                results.Add(new BlockClassification(id, type, headingLevel == 0 ? null : headingLevel, confidence));
            }

            return results;
        }
    }

    private static bool TryMapType(string value, out BlockType type)
    {
        foreach (var (key, mapped) in TypeMap)
        {
            if (key == value)
            {
                type = mapped;
                return true;
            }
        }

        type = default;
        return false;
    }

    private static string ToKey(BlockType type)
    {
        foreach (var (key, mapped) in TypeMap)
        {
            if (mapped == type)
            {
                return key;
            }
        }

        return "body";
    }

    private static JsonElement BuildSchema()
    {
        const string schemaJson = """
            {
              "type": "object",
              "properties": {
                "blocks": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "id": { "type": "string" },
                      "type": {
                        "type": "string",
                        "enum": ["chapter_title", "section_heading", "subheading", "body", "aside", "pull_quote", "caption", "footnote", "decorative", "header", "footer", "page_number"]
                      },
                      "headingLevel": { "type": "integer" },
                      "confidence": { "type": "number" }
                    },
                    "required": ["id", "type", "headingLevel", "confidence"]
                  }
                }
              },
              "required": ["blocks"]
            }
            """;

        return JsonDocument.Parse(schemaJson).RootElement.Clone();
    }
}

public sealed record BlockClassification(string Id, BlockType Type, int? HeadingLevel, double Confidence);
