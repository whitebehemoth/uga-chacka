using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WhiteBehemoth.Yara.Settings;

public class HomographConfig : INotifyPropertyChanged
{
    public double Threshold
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public string DictionaryPath
    {
        get => field ?? "";
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public List<string> DicAPath
    {
        get => field ?? [];
        set
        {
            var current = field ?? [];
            var incoming = value ?? [];
            if (!current.SequenceEqual(incoming))
            {
                field = value;
                OnPropertyChanged();
            }
        }
    }

    public string CleanRegexPath
    {
        get => field ?? "";
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
