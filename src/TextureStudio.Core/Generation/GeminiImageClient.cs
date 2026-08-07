using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TextureStudio.Core.Generation;

/// <summary>Minimal Gemini image-generation client (generateContent with inline images).
/// The API key is supplied per call and never persisted by Core.</summary>
public sealed class GeminiImageClient(HttpClient http)
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    public async Task<byte[]> GenerateImageAsync(
        string apiKey,
        string modelId,
        string prompt,
        IReadOnlyList<byte[]> inputPngs,
        string? imageSize,
        double? topP = null,
        string? thinkingLevel = null,
        CancellationToken ct = default)
    {
        var parts = new List<object> { new { text = prompt } };
        parts.AddRange(inputPngs.Select(png => (object)new
        {
            inline_data = new { mime_type = "image/png", data = Convert.ToBase64String(png) },
        }));
        var generationConfig = new Dictionary<string, object>
        {
            ["responseModalities"] = new[] { "IMAGE" },
        };
        if (imageSize is not null)
        {
            generationConfig["imageConfig"] = new { imageSize };
        }
        if (topP is not null)
        {
            generationConfig["topP"] = topP.Value;
        }
        if (!string.IsNullOrWhiteSpace(thinkingLevel))
        {
            generationConfig["thinkingConfig"] = new { thinkingLevel };
        }
        var request = new
        {
            contents = new[] { new { parts } },
            generationConfig,
        };
        using var response = await http.PostAsJsonAsync(
            $"{BaseUrl}/models/{modelId}:generateContent?key={Uri.EscapeDataString(apiKey)}",
            request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Gemini API {(int)response.StatusCode}: {Truncate(body)}");
        }
        // Blocked or refused responses omit "candidates"/"content"/"parts" — never assume
        // the happy-path shape; dig out the reason instead so the job error is actionable.
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("promptFeedback", out var feedback) &&
            feedback.TryGetProperty("blockReason", out var blockReason))
        {
            throw new InvalidOperationException($"Gemini blocked the prompt: {blockReason.GetString()}.");
        }
        string? finishReason = null;
        string? textReply = null;
        if (root.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in candidates.EnumerateArray())
            {
                if (finishReason is null &&
                    candidate.TryGetProperty("finishReason", out var fr))
                {
                    finishReason = fr.GetString();
                }
                if (!candidate.TryGetProperty("content", out var content) ||
                    !content.TryGetProperty("parts", out var responseParts))
                {
                    continue;
                }
                foreach (var part in responseParts.EnumerateArray())
                {
                    if (part.TryGetProperty("inlineData", out var inline) ||
                        part.TryGetProperty("inline_data", out inline))
                    {
                        return Convert.FromBase64String(inline.GetProperty("data").GetString()!);
                    }
                    if (textReply is null && part.TryGetProperty("text", out var text))
                    {
                        textReply = text.GetString();
                    }
                }
            }
        }
        var reason = finishReason is null or "STOP" ? null : $"finish reason {finishReason}";
        var detail = (reason, textReply) switch
        {
            (not null, not null) => $"{reason}; model said: \"{Truncate(textReply)}\"",
            (not null, null) => reason,
            (null, not null) => $"model replied with text instead: \"{Truncate(textReply)}\"",
            _ => $"response: {Truncate(body)}",
        };
        throw new InvalidOperationException($"Gemini returned no image ({detail}).");
    }

    public async Task<List<string>> ListImageModelsAsync(string apiKey, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{BaseUrl}/models?key={Uri.EscapeDataString(apiKey)}&pageSize=200", ct);
        response.EnsureSuccessStatusCode();
        var models = await response.Content.ReadFromJsonAsync<ModelList>(ct) ?? new ModelList();
        return models.Models
            .Where(m => m.SupportedGenerationMethods.Contains("generateContent"))
            .Select(m => m.Name.Replace("models/", ""))
            .Where(n => n.Contains("image", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string Truncate(string s) => s.Length <= 400 ? s : s[..400] + "…";

    private sealed class ModelList
    {
        [JsonPropertyName("models")]
        public List<ModelInfo> Models { get; set; } = [];
    }

    private sealed class ModelInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("supportedGenerationMethods")]
        public List<string> SupportedGenerationMethods { get; set; } = [];
    }
}
