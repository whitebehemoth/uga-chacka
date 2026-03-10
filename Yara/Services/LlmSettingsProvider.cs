using HomographResolver;
using Microsoft.Extensions.Options;
using WhiteBehemoth.Yara.Settings;

namespace WhiteBehemoth.Yara.Services;

/// <summary>
/// Bridges the new multi-config LlmConfig into the existing
/// LlmSettings that OpenAiLlmClient / FoundryLocalLlmClient expect.
/// </summary>
public sealed class LlmSettingsProvider : IOptionsMonitor<LlmSettings>
{
    private readonly IOptionsMonitor<AppSettings> _appSettings;

    public LlmSettingsProvider(IOptionsMonitor<AppSettings> appSettings)
    {
        _appSettings = appSettings;
    }

    public LlmSettings CurrentValue => Build();

    public LlmSettings Get(string? name) => Build();

    public IDisposable? OnChange(Action<LlmSettings, string?> listener) => null;

    private LlmSettings Build()
    {
        var llm = _appSettings.CurrentValue.Llm;
        var settings = new LlmSettings
        {
            Temperature = llm.Temperature,
            SystemPrompt = llm.SystemPrompt
        };

        var provider = llm.SelectedProvider ?? "";

        if (provider.StartsWith("foundry:"))
        {
            settings.Type = "FoundryLocal";
            settings.FoundryModel = provider[8..];
        }
        else
        {
            int idx = 0;
            if (provider.StartsWith("openai:") && int.TryParse(provider[7..], out var parsed))
                idx = parsed;

            if (idx >= 0 && idx < llm.OpenAiEndpoints.Count)
            {
                var ep = llm.OpenAiEndpoints[idx];
                settings.Type = "OpenAI";
                settings.Url = ep.Url;
                settings.Model = ep.Model;
                settings.ApiKey = ep.ApiKey;
            }
        }

        return settings;
    }
}
