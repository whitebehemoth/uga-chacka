using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WhiteBehemoth.Yara.Settings;

public class GeneralConfig : INotifyPropertyChanged
{
    public double? DefaultFontSize
    {
        get => field ?? 14;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }
    public string TargetFolder
    {
        get => field ?? "output";
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }
    
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
