using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WhiteBehemoth.Yara.Settings;

public class LlmConfig : INotifyPropertyChanged
{
    public string SelectedProvider
    {
        get => field ?? "openai:0";
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public List<OpenAiEndpoint> OpenAiEndpoints
    {
        get => field ?? [];
        set { field = value; OnPropertyChanged(); }
    }

    public double Temperature
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public string SystemPrompt
    {
        get => field ?? "";
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public List<string> KnownFoundryModels
    {
        get => field ?? [];
        set { field = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Returns display string for status bar: "URL — Model".</summary>
    public string GetActiveDisplayName()
    {
        var provider = SelectedProvider ?? "";

        if (provider.StartsWith("foundry:"))
            return $"FoundryLocal — {provider[8..]}";

        int idx = 0;
        if (provider.StartsWith("openai:") && int.TryParse(provider[7..], out var parsed))
            idx = parsed;

        if (idx >= 0 && idx < OpenAiEndpoints.Count)
        {
            var ep = OpenAiEndpoints[idx];
            var host = ep.Url;
            try { host = new Uri(ep.Url).Host; } catch { }
            return $"{host} — {ep.Model}";
        }

        return "не настроен";
    }
}
