using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace HomographResolver;

public class HomographVariant
{
    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("gram_def")]
    public List<string> GramDef { get; set; } = [];

    [JsonPropertyName("frequency")]
    public int Frequency { get; set; }

    [JsonPropertyName("lemma")]
    public string Lemma { get; set; } = "";

    [JsonPropertyName("stress_pos")]
    public int StressPos { get; set; }

    [JsonPropertyName("lemma_def")]
    public List<string> LemmatDef { get; set; } = [];
}
public class ResolvedHomograph
{
    public string OriginalWord { get; set; } = "";
    public string StressedWord { get; set; } = "";
    public int ChosenIndex { get; set; }
    public double Confidence { get; set; }
    public string Reasoning { get; set; } = "";
    public int OriginalPosition { get; set; }
    public int OriginalLength { get; set; }
    public int AbsolutePosition { get; set; }
    public int Length { get; set; }
    public List<HomographVariant> Variants { get; set; } = [];
}

public class LlmChoice
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("reasoning")]
    public string Reasoning { get; set; } = "";
}

public class LlmSettings : INotifyPropertyChanged
{
    public string Type
    {
        get => field ?? "";
        set { field = value; OnPropertyChanged(); }
    }

    public string Url
    {
        get => field ?? "";
        set { field = value; OnPropertyChanged(); }
    }

    public string Model
    {
        get => field ?? "";
        set { field = value; OnPropertyChanged(); }
    }

    public string FoundryModel
    {
        get => field ?? "";
        set { field = value; OnPropertyChanged(); }
    }

    public string ApiKey
    {
        get => field ?? "";
        set { field = value; OnPropertyChanged(); }
    }

    public double Temperature
    {
        get => field;
        set { field = value; OnPropertyChanged(); }
    }

    public string SystemPrompt
    {
        get => field ?? "";
        set { field = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
