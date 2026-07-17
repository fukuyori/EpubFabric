using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace EpubFabric.Ollama;

/// <summary>
/// 13章 Ollama連携：ローカルのOllamaサーバーとの通信を担当する薄いクライアント。
/// </summary>
public sealed class OllamaClient
{
    private readonly HttpClient _httpClient;

    public OllamaClient(string endpoint, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.BaseAddress = new Uri(endpoint);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/api/version", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// 13.3 構造化出力：JSON Schemaでフォーマットを指定してテキスト生成を行う。
    /// </summary>
    public async Task<string> GenerateAsync(
        string model,
        string prompt,
        JsonElement? formatSchema = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["stream"] = false,
        };

        if (formatSchema is not null)
        {
            requestBody["format"] = formatSchema;
        }

        using var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is not null)
        {
            cts.CancelAfter(timeout.Value);
        }

        using var response = await _httpClient.PostAsync("/api/generate", content, cts.Token);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
        return responseJson.GetProperty("response").GetString() ?? string.Empty;
    }
}
