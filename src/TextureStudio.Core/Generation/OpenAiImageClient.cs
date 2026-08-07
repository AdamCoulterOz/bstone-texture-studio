using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TextureStudio.Core.Generation;

/// <summary>Minimal OpenAI Images client for the GPT Image models. Generation with
/// reference inputs goes through /v1/images/edits (multipart, image[] files); prompt-only
/// generation uses /v1/images/generations. The Effort level maps onto the API's quality
/// parameter (low/medium/high).</summary>
public sealed class OpenAiImageClient(HttpClient http)
{
    private const string BaseUrl = "https://api.openai.com/v1";

    public static bool IsOpenAiModel(string modelId) =>
        modelId.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase);

    /// <summary>Square output size for a composed sheet: gpt-image-2 supports flexible
    /// resolutions (16px multiples, ≤3840 edge); the earlier models cap at 1024².</summary>
    public static string SizeFor(string modelId, int canvasPx)
    {
        if (!modelId.StartsWith("gpt-image-2", StringComparison.OrdinalIgnoreCase))
        {
            return "1024x1024";
        }
        return canvasPx <= 1024 ? "1024x1024" : canvasPx <= 2048 ? "2048x2048" : "3072x3072";
    }

    public async Task<byte[]> GenerateImageAsync(
        string apiKey,
        string modelId,
        string prompt,
        IReadOnlyList<byte[]> inputPngs,
        string size,
        string? quality = null,
        CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            inputPngs.Count > 0 ? $"{BaseUrl}/images/edits" : $"{BaseUrl}/images/generations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (inputPngs.Count > 0)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(modelId), "model");
            form.Add(new StringContent(prompt), "prompt");
            form.Add(new StringContent(size), "size");
            if (quality is { Length: > 0 })
            {
                form.Add(new StringContent(quality), "quality");
            }
            for (var i = 0; i < inputPngs.Count; i++)
            {
                var file = new ByteArrayContent(inputPngs[i]);
                file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(file, "image[]", $"input-{i}.png");
            }
            request.Content = form;
        }
        else
        {
            var payload = new Dictionary<string, object> { ["model"] = modelId, ["prompt"] = prompt, ["size"] = size };
            if (quality is { Length: > 0 })
            {
                payload["quality"] = quality;
            }
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        }
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            // Message shape matters: the retry classifier looks for " 429"/" 5xx".
            throw new HttpRequestException($"OpenAI API {(int)response.StatusCode}: {Truncate(body)}");
        }
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 &&
            data[0].TryGetProperty("b64_json", out var b64) &&
            b64.GetString() is { Length: > 0 } encoded)
        {
            return Convert.FromBase64String(encoded);
        }
        throw new InvalidOperationException($"OpenAI returned no image ({Truncate(body)}).");
    }

    private static string Truncate(string s) =>
        s.Length <= 300 ? s : s[..300];
}
