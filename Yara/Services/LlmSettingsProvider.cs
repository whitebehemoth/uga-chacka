using Microsoft.Extensions.Options;
using WhiteBehemoth.Resolver.Llm;
using WhiteBehemoth.Yara.Settings;

namespace WhiteBehemoth.Yara.Services;

/// <summary>
/// Converts LlmConfig (WPF/settings model) to LlmSettings (resolver library record).
/// Used as Func&lt;LlmSettings&gt; delegate in DI.
/// </summary>
public sealed class LlmSettingsProvider(IOptionsMonitor<AppSettings> appSettings)
{
    public LlmSettings CurrentValue => Build(appSettings.CurrentValue.Llm);

    private static LlmSettings Build(LlmConfig llm)
    {
        var provider = llm.SelectedProvider ?? "";

        if (provider.StartsWith("foundry:"))
            return new LlmSettings(
                Type: "FoundryLocal",
                FoundryModel: provider[8..],
                Temperature: llm.Temperature,
                SystemPrompt: llm.SystemPrompt);

        int idx = 0;
        if (provider.StartsWith("openai:") && int.TryParse(provider[7..], out var parsed))
            idx = parsed;

        if (idx >= 0 && idx < llm.OpenAiEndpoints.Count)
        {
            var ep = llm.OpenAiEndpoints[idx];
            return new LlmSettings(
                Type: "OpenAI",
                Url: ep.Url,
                Model: ep.Model,
                ApiKey: ep.ApiKey,
                Temperature: llm.Temperature,
                SystemPrompt: llm.SystemPrompt);
        }

        return new LlmSettings(Temperature: llm.Temperature, SystemPrompt: llm.SystemPrompt);
    }
}
