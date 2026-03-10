using System.ComponentModel;

namespace WhiteBehemoth.Yara.Models;

public sealed class CleanRule : INotifyPropertyChanged
{
    private bool _isSelected = true; // checked by default per spec

    public CleanRule(string description, string pattern, string replacement)
    {
        Description = description;
        Pattern = pattern;
        Replacement = replacement;
    }

    public string Description { get; }
    public string Pattern { get; }
    public string Replacement { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
