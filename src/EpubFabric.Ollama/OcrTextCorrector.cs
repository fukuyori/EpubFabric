using System.Text;
using System.Text.Json;
using EpubFabric.Core.Models;

namespace EpubFabric.Ollama;

/// <summary>
/// OCR誤認識のOllamaによる後処理校正：OCR由来ブロックの文字列を文脈から校正する
/// （例: 解脱→解説、棗境→環境、入カ→入力）。分類（PageBlockClassifier）と同じく
/// 13.3節の構造化出力と13.4節の検証を経て初めて適用する。要約・言い換え・加筆は
/// させず、誤認識文字の置き換えのみを行わせる。LLMが文章を書き換えてしまう事故を
/// 防ぐため、編集距離が原文に対して大きすぎる修正案は破棄する。
/// 手動修正済み（CorrectedTextあり）のブロックは対象にしない（9.9節:
/// 手動修正した項目は再解析によって上書きしない）。
/// </summary>
public sealed class OcrTextCorrector
{
    /// <summary>1回のリクエストで校正するブロック数。固定レイアウトでは1ページが100行を超える
    /// ことがあり、一度に送るとローカルLLMの応答品質・速度が落ちるため分割する。</summary>
    private const int BlocksPerRequest = 20;

    /// <summary>原文に対する変更文字数の許容割合。これを超える修正案は言い換え・創作とみなして破棄する。</summary>
    private const double MaxChangedCharRatio = 0.35;

    /// <summary>この長さ以上の英数字連続列（URL・DOI・型番・数値）内の変更は拒否する。
    /// 実データ検証でLLMがURLの一部削除やarXiv番号の書き換えを行う事故が確認されたため。
    /// 「A1→AI」のような短い列の誤認識修正は許容する。</summary>
    private const int ProtectedAsciiRunLength = 3;

    private static readonly JsonElement ResponseSchema = BuildSchema();

    private readonly OllamaClient _client;
    private readonly string _model;
    private readonly TimeSpan _timeout;
    private readonly int _maxRetryCount;

    public OcrTextCorrector(OllamaClient client, string model, TimeSpan? timeout = null, int maxRetryCount = 2)
    {
        _client = client;
        _model = model;
        _timeout = timeout ?? TimeSpan.FromSeconds(120);
        _maxRetryCount = maxRetryCount;
    }

    /// <summary>
    /// ページ内のOCR由来ブロックを校正し、検証を通った修正だけをCorrectedTextへ反映する。
    /// 戻り値は実際に修正されたブロック数。
    /// </summary>
    public async Task<int> CorrectPageAsync(DocumentPage page, CancellationToken cancellationToken = default)
    {
        var candidates = page.Blocks
            .Where(b => b.TextSource == TextSourceKind.Ocr
                && !b.IsExcluded
                && b.CorrectedText is null
                && b.Type is not (BlockType.Figure or BlockType.Table or BlockType.Decorative)
                && !string.IsNullOrWhiteSpace(b.OcrText))
            .OrderBy(b => b.ReadingOrder)
            .ToList();

        var correctedCount = 0;
        var chunkCount = 0;
        var failedChunkCount = 0;
        OllamaClassificationException? lastFailure = null;

        // 1チャンクの失敗（タイムアウト等）でページ内の他のチャンクの校正まで
        // 打ち切らない。全チャンクが失敗した場合のみ失敗として報告する。
        foreach (var chunk in candidates.Chunk(BlocksPerRequest))
        {
            chunkCount++;
            try
            {
                correctedCount += await CorrectChunkAsync(chunk, cancellationToken);
            }
            catch (OllamaClassificationException ex)
            {
                failedChunkCount++;
                lastFailure = ex;
            }
        }

        if (chunkCount > 0 && failedChunkCount == chunkCount)
        {
            throw lastFailure!;
        }

        return correctedCount;
    }

    private async Task<int> CorrectChunkAsync(IReadOnlyList<PageBlock> blocks, CancellationToken cancellationToken)
    {
        var prompt = BuildPrompt(blocks);

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

            var corrections = Validate(raw, blocks);
            if (corrections is null)
            {
                lastError = new OllamaClassificationException($"Ollamaの校正応答を検証できませんでした: {raw}");
                continue;
            }

            var byId = blocks.ToDictionary(b => b.Id);
            foreach (var (id, corrected) in corrections)
            {
                byId[id].CorrectedText = corrected;
            }

            return corrections.Count;
        }

        throw new OllamaClassificationException(
            $"OllamaによるOCR校正に{_maxRetryCount + 1}回失敗しました。OCR結果をそのまま使用してください。",
            lastError ?? new InvalidOperationException("unknown error"));
    }

    private static string BuildPrompt(IReadOnlyList<PageBlock> blocks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("あなたは、紙面をスキャンしOCRした電子書籍制作システムの一部として、");
        sb.AppendLine("OCRの誤認識文字を校正するアシスタントです。");
        sb.AppendLine();
        sb.AppendLine("以下は日本語文書のOCR結果のブロック一覧です。形の似た文字の誤認識");
        sb.AppendLine("（例: 解説→解脱、環境→棗境、入力→入カ、ペ→ベ、ー→一）だけを修正してください。");
        sb.AppendLine();
        sb.AppendLine("厳守事項:");
        sb.AppendLine("- 誤認識と確信できる文字の1対1の置き換えのみを行うこと。文字数を変えない（挿入・削除をしない）こと。");
        sb.AppendLine("- 言い換え・要約・加筆・削除・語順の変更をしないこと。");
        sb.AppendLine("- URL・DOI・番号などの英数字の並びは、間違って見えても修正しないこと。");
        sb.AppendLine("- 句読点や記号のスタイルを整えないこと（原文のまま残す）。");
        sb.AppendLine("- 各行は段組みの1行の断片で、文の途中で切れていることがある。断片として不自然なだけなら修正しないこと。");
        sb.AppendLine("- 誤認識が見つからないブロックは、correctedに原文をそのまま入れること。");
        sb.AppendLine();
        sb.AppendLine("ブロック一覧:");

        foreach (var block in blocks)
        {
            var text = block.OcrText.Replace('\n', ' ');
            sb.AppendLine($"- id: {block.Id}, テキスト: \"{text}\"");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 応答を検証し、実際に文字が変わった安全な修正だけを返す。分類と同じ方針で、
    /// 項目単位の不備はその1件だけ読み捨てる。原文からの編集距離が大きすぎる修正案は
    /// 言い換えの疑いがあるため破棄する。
    /// </summary>
    private static List<(string Id, string Corrected)>? Validate(string raw, IReadOnlyList<PageBlock> blocks)
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

            var byId = blocks.ToDictionary(b => b.Id);
            var seenIds = new HashSet<string>();
            var results = new List<(string Id, string Corrected)>();

            foreach (var item in blocksElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("corrected", out var correctedElement) || correctedElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var id = idElement.GetString()!;
                if (!byId.TryGetValue(id, out var block) || !seenIds.Add(id))
                {
                    continue;
                }

                var corrected = correctedElement.GetString()!;
                var original = block.OcrText.Replace('\n', ' ');

                if (corrected == original || !IsSafeCorrection(original, corrected))
                {
                    continue;
                }

                results.Add((id, corrected));
            }

            return results;
        }
    }

    /// <summary>
    /// 修正案が安全な「文字置換のみ」かを判定する。実データ検証（gemma4:12b・科学202601）で
    /// 確認された改悪パターン — URLの一部削除、arXiv番号の書き換え、単語の重複挿入、
    /// 助詞の挿入 — を弾くため、(1) 文字数が変わる修正（挿入・削除）を全拒否、
    /// (2) 長い英数字連続列の内側の変更を拒否、(3) 変更文字数が多すぎる案を拒否する。
    /// 日本語OCRの誤認識は形が似た文字への1対1置換（悄→情、貴→責、腾→騰）が
    /// ほとんどのため、この制限による取りこぼしは小さい。
    /// </summary>
    private static bool IsSafeCorrection(string original, string corrected)
    {
        if (original.Length != corrected.Length)
        {
            return false;
        }

        var changedCount = 0;
        for (var i = 0; i < original.Length; i++)
        {
            if (original[i] == corrected[i])
            {
                continue;
            }

            changedCount++;
            if (IsInProtectedAsciiRun(original, i))
            {
                return false;
            }

            // ひらがな→ひらがなの置換は拒否する。OCRの誤認識は字形が似た漢字・カタカナ・
            // 英数字の混同がほとんどで、ひらがな同士の混同はまれ。一方、実データ検証では
            // LLMが行断片の文法を「直そう」として助詞・活用を書き換える改悪
            // （「したがるのは」→「したがらのは」、「ればよい」→「ればいる」）が
            // このパターンに集中した。
            if (IsHiragana(original[i]) && IsHiragana(corrected[i]))
            {
                return false;
            }
        }

        return changedCount <= Math.Max(2, original.Length * MaxChangedCharRatio);
    }

    private static bool IsHiragana(char c) => c is >= 'ぁ' and <= 'ゟ';

    /// <summary>指定位置が長さ<see cref="ProtectedAsciiRunLength"/>以上の英数字連続列
    /// （URL・DOI・数値等。連結記号 ./-:_ を含む）の内側にあるか。</summary>
    private static bool IsInProtectedAsciiRun(string text, int index)
    {
        static bool IsRunChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '/' or '-' or ':' or '_';

        if (!IsRunChar(text[index]))
        {
            return false;
        }

        var start = index;
        while (start > 0 && IsRunChar(text[start - 1]))
        {
            start--;
        }

        var end = index;
        while (end < text.Length - 1 && IsRunChar(text[end + 1]))
        {
            end++;
        }

        return end - start + 1 >= ProtectedAsciiRunLength;
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
                      "corrected": { "type": "string" }
                    },
                    "required": ["id", "corrected"]
                  }
                }
              },
              "required": ["blocks"]
            }
            """;

        return JsonDocument.Parse(schemaJson).RootElement.Clone();
    }
}
