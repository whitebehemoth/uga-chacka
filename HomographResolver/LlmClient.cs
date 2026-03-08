using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace HomographResolver;

public sealed class OpenAiLlmClient : ILlmClient, IDisposable
{
    private readonly HttpClient _http = new();
    private readonly IOptionsMonitor<LlmSettings> _settings;

    public OpenAiLlmClient(IOptionsMonitor<LlmSettings> settings)
    {
        _settings = settings;
    }

    public async Task<LlmChoice> ResolveHomographAsync(
        string context, string word, List<HomographVariant> variants,
        CancellationToken ct = default)
    {
        var settings = _settings.CurrentValue;
        var userPrompt = LlmPromptBuilder.BuildUserPrompt(context, word, variants);

        var request = new
        {
            model = settings.Model,
            temperature = settings.Temperature,
            messages = new object[]
            {
                new { role = "system", content = settings.SystemPrompt },
                new { role = "user", content = userPrompt }
            }
        };
        JsonElement json;
        try
        {
            var url = settings.Url;
            using var message = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(request)
            };

            if (!string.IsNullOrEmpty(settings.ApiKey))
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

            var response = await _http.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (Exception ex) {
            throw new Exception("Ошибка при вызове LLM API: " + ex.Message, ex);
        }

        var content = json.GetProperty("choices")[0]
                         .GetProperty("message")
                         .GetProperty("content")
                         .GetString();

        try
        {
            var jsonStart = content!.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonStr = content[jsonStart..(jsonEnd + 1)];
                var choice = JsonSerializer.Deserialize<LlmChoice>(jsonStr);
                if (choice != null && choice.Index >= 0 && choice.Index <= variants.Count)
                    return choice;
            }
        }
        catch { /* parse error — fall through to default */ }

        return new LlmChoice { Index = 0, Confidence = 0.0 };
    }

    public void Dispose() => _http.Dispose();
}
