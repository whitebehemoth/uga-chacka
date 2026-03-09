using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.Json;
using HomographResolver;

namespace uga_chacka;

public class AppSettings : INotifyPropertyChanged, IDisposable
{
    private Timer? _saveTimer;
    private const int SaveDelayMs = 500; // Debounce: сохранять не чаще чем раз в 500ms

    private bool _disposed;
    private bool _autoSaveEnabled;

    private void NestedSettingChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSave();

    public void EnableAutoSave()
    {
        _autoSaveEnabled = true;
    }

    public LlmSettings Llm
    {
        get => field!;
        set 
        { 
            if (field != value)
            {
                if (field != null)
                    field.PropertyChanged -= NestedSettingChanged;

                field = value;

                if (field != null)
                    field.PropertyChanged += NestedSettingChanged;

                OnPropertyChanged();
                ScheduleSave();
            }
        }
    }

    public TtsConfig Tts
    {
        get => field!;
        set 
        { 
            if (field != value)
            {
                if (field != null)
                    field.PropertyChanged -= NestedSettingChanged;

                field = value;

                if (field != null)
                    field.PropertyChanged += NestedSettingChanged;

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
                if (field != null)
                    field.PropertyChanged -= NestedSettingChanged;

                field = value;

                if (field != null)
                    field.PropertyChanged += NestedSettingChanged;

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
                if (field != null)
                    field.PropertyChanged -= NestedSettingChanged;

                field = value;

                if (field != null)
                    field.PropertyChanged += NestedSettingChanged;

                OnPropertyChanged();
                ScheduleSave();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public virtual void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _saveTimer?.Dispose();
        _disposed = true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected virtual void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    private void ScheduleSave()
    {
        if (!_autoSaveEnabled)
            return;

        // Отмена предыдущего таймера
        _saveTimer?.Dispose();

        // Запланировать сохранение через 500ms
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
        catch
        {
            // Ignore save errors
        }
    }
}

public class TtsConfig : INotifyPropertyChanged
{

    public string Type
    {
        get => field ?? "";
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
    }

    public string Url
    {
        get => field ?? "";
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
    }

    public string VoicePath
    {
        get => field ?? "";
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public class HomographConfig : INotifyPropertyChanged
{
    public double Threshold
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
    }

    public string DictionaryPath
    {
        get => field ?? "";
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
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

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public class GeneralConfig : INotifyPropertyChanged
{
    public double? DefaultFontSize
    {
        get => field ?? 14;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
