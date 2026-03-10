using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace WhiteBehemoth.Yara.Settings;

public class AppSettings : INotifyPropertyChanged, IDisposable
{
    private Timer? _saveTimer;
    private const int SaveDelayMs = 500;
    private bool _disposed;
    private bool _autoSaveEnabled;

    private void NestedSettingChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSave();

    public void EnableAutoSave() => _autoSaveEnabled = true;

    public LlmConfig Llm
    {
        get => field!;
        set
        {
            if (field != value)
            {
                if (field != null) field.PropertyChanged -= NestedSettingChanged;
                field = value;
                if (field != null) field.PropertyChanged += NestedSettingChanged;
                OnPropertyChanged();
                ScheduleSave();
            }
        }
    }

    public HomographConfig Homograph
    {
        get => field!;
        set
        {
            if (field != value)
            {
                if (field != null) field.PropertyChanged -= NestedSettingChanged;
                field = value;
                if (field != null) field.PropertyChanged += NestedSettingChanged;
                OnPropertyChanged();
                ScheduleSave();
            }
        }
    }

    public GeneralConfig General
    {
        get => field!;
        set
        {
            if (field != value)
            {
                if (field != null) field.PropertyChanged -= NestedSettingChanged;
                field = value;
                if (field != null) field.PropertyChanged += NestedSettingChanged;
                OnPropertyChanged();
                ScheduleSave();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        if (_disposed) return;
        _saveTimer?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void ScheduleSave()
    {
        if (!_autoSaveEnabled) return;
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ => SaveSettings(), null, SaveDelayMs, Timeout.Infinite);
    }

    private void SaveSettings()
    {
        try
        {
            _saveTimer?.Dispose();
            _saveTimer = null;

            var json = JsonSerializer.Serialize(new { AppSettings = this }, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(App.AppSettingsPath, json);
        }
        catch { }
    }
}
