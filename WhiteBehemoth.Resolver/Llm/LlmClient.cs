using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using WhiteBehemoth.Resolver.Models;

namespace WhiteBehemoth.Resolver.Llm;

public sealed class OpenAiLlmClient : ILlmClient, IDisposable
{
    private readonly HttpClient _http = new();
    private readonly Func<LlmSettings> _getSettings;

    public OpenAiLlmClient(Func<LlmSettings> getSettings)
    {
        _getSettings = getSettings;
    }

    public async Task<string> ResolveSentenceStressAsync(string context, CancellationToken ct = default)
    {
        var settings = _getSettings();
        var userPrompt = LlmPromptBuilder.BuildSentenceStressPrompt(context);
        var content = await CompleteChatAsync(settings.SentenceStressSystemPrompt, userPrompt, ct);
        return content.Trim();
    }

    public async Task<LlmChoice> ResolveHomographAsync(
        string context, string word, List<HomographVariant> variants,
        CancellationToken ct = default)
    {
        var settings = _getSettings();
        var userPrompt = LlmPromptBuilder.BuildUserPrompt(context, word, variants);
        var content = await CompleteChatAsync(settings.SystemPrompt, userPrompt, ct);

        try
        {
            var jsonStart = content!.IndexOf('{');
            var jsonEnd = content.LastIndexOf('}');
            var jsonStr = content[jsonStart..(jsonEnd + 1)];
            var choice = JsonSerializer.Deserialize<LlmChoice>(jsonStr);
            return choice ?? throw new Exception("Ошибка при Deserialize ответа LLM: ");
        }
        catch (Exception ex)
        {
            throw new Exception("Ошибка при парсинге ответа LLM: " + ex.Message, ex);
        }
    }

    private async Task<string> CompleteChatAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var settings = _getSettings();
        var request = new
        {
            model = settings.Model,
            temperature = settings.Temperature,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        JsonElement json;
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, settings.Url)
            {
                Content = JsonContent.Create(request)
            };

            if (!string.IsNullOrEmpty(settings.ApiKey))
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

            using var response = await _http.SendAsync(message, ct);
            if ((int)response.StatusCode == 429)
                throw new LlmRateLimitException("429 Too Many Requests");

            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (LlmRateLimitException) { throw; }
        catch (Exception ex)
        {
            throw new Exception("Ошибка при вызове LLM API: " + ex.Message, ex);
        }

        var content = json.GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return content ?? string.Empty;
    }

    public void Dispose() => _http.Dispose();
}
