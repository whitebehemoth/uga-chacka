using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WhiteBehemoth.Yara.Settings;

public class OpenAiEndpoint : INotifyPropertyChanged
{
    public string Name
    {
        get => field ?? "";
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public string Url
    {
        get => field ?? "";
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public string Model
    {
        get => field ?? "";
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public string ApiKey
    {
        get => field ?? "";
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Url : Name;
}
