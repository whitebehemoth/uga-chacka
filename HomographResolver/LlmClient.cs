using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace HomographResolver;

public class LlmClient : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly LlmSettings _settings;

    public LlmClient(LlmSettings settings)
    {
        _settings = settings;
        if (!string.IsNullOrEmpty(settings.ApiKey))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.ApiKey);
    }

    public async Task<LlmChoice> ResolveHomographAsync(
        string context, string word, List<HomographVariant> variants,
        CancellationToken ct = default)
    {
        var userPrompt = BuildUserPrompt(context, word, variants);

        var request = new
        {
            model = _settings.Model,
            temperature = _settings.Temperature,
            messages = new object[]
            {
                new { role = "system", content = _settings.SystemPrompt },
                new { role = "user", content = userPrompt }
            }
        };
        JsonElement json;
        try
        {
            var url = _settings.Url;
            var response = await _http.PostAsJsonAsync(url, request, ct);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync();
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
                if (choice != null && choice.Index >= 1 && choice.Index <= variants.Count)
                    return choice;
            }
        }
        catch { /* parse error — fall through to default */ }

        return new LlmChoice { Index = 1, Confidence = 0.0 };
    }

    private static string BuildUserPrompt(
        string context, string word, List<HomographVariant> variants)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Предложение: \"{context}\"");
        sb.AppendLine($"Слово-омограф: \"{word}\"");
        sb.AppendLine();
        sb.AppendLine("Варианты:");
        foreach (var v in variants)
        {
            sb.Append($"{v.Index}.");
            sb.Append($" — грамматика: {string.Join("; ", v.GramDef)}");
            if (v.LemmatDef.Count > 0)
                sb.Append($" — значение леммы: {string.Join("; ", v.LemmatDef)}");
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("Какой вариант правильный?");
        return sb.ToString();
    }

    public void Dispose() => _http.Dispose();
}
