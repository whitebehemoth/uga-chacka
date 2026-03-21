using System.IO;
using System.Text.Json;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhiteBehemoth.Resolver.Llm;
using WhiteBehemoth.Yara.Services;
using WhiteBehemoth.Yara.Settings;

namespace WhiteBehemoth.Yara;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private static IConfigurationRoot? _configuration;
    private static int _fatalErrorHandled;

    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (!TryInitializeConfiguration())
        {
            Shutdown(-1);
            return;
        }

        RegisterGlobalExceptionHandlers();

        try
        {
            Services ??= ConfigureServices(Configuration);

            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
            base.OnStartup(e);
        }
        catch (Exception ex)
        {
            HandleFatalException(ex, "startup");
            Shutdown(-1);
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_UnobservedTaskException;

        if (Services is IDisposable disposable)
            disposable.Dispose();
        base.OnExit(e);
    }

    public static string AppSettingsPath { get; } = Path.Combine(
        AppContext.BaseDirectory, "appsettings.json");

    public static IConfigurationRoot Configuration => _configuration
        ?? throw new InvalidOperationException("Конфигурация не инициализирована.");

    private static IConfigurationRoot BuildConfiguration() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile(AppSettingsPath, optional: true, reloadOnChange: true)
        .AddUserSecrets<App>(optional: true)
        .Build();

    private static bool TryInitializeConfiguration()
    {
        EnsureConfigFileExists();

        try
        {
            _configuration = BuildConfiguration();
            return true;
        }
        catch (Exception ex)
        {
            var message =
                "Конфигурация приложения повреждена и не может быть прочитана.\n\n" +
                $"Файл: {AppSettingsPath}\n" +
                $"Ошибка: {ex.Message}\n\n" +
                "Да — удалить повреждённый конфиг и выйти.\n" +
                "Нет — открыть файл для ручного исправления и выйти.\n" +
                "Отмена — просто выйти.";

            var result = MessageBox.Show(
                message,
                "Ошибка конфигурации",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                TryDeleteBrokenConfig();
            }
            else if (result == MessageBoxResult.No)
            {
                TryOpenConfigForEdit();
            }

            return false;
        }
    }

    private static void EnsureConfigFileExists()
    {
        if (File.Exists(AppSettingsPath))
            return;

        var defaultConfig = new
        {
            AppSettings = new
            {
                Llm = new
                {
                    SelectedProvider = "openai:0",
                    OpenAiEndpoints = new[]
                    {
                        new
                        {
                            Name = "OpenAI",
                            Url = "https://api.openai.com/v1",
                            Model = "gpt-4.1-mini",
                            ApiKey = ""
                        }
                    },
                    Temperature = 1,
                    NextRequestInMs = 500,
                    KnownFoundryModels = Array.Empty<string>(),
                    SystemPrompt = ""
                },
                Homograph = new
                {
                    Threshold = 96,
                    DictionaryPath = "dics\\dic.json",
                    DictionaryPhrasesPath = "dics\\dic-alp.json",
                    DicAPath = new[]
                    {
                        "dics\\dic-a2l.json",
                        "dics\\dic-yo.json",
                        "dics\\dic-al.json"
                    },
                    CleanRegexPath = "dics\\clean.rex"
                },
                General = new
                {
                    DefaultFontSize = 14,
                    TargetFolder = "output"
                }
            }
        };

        var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        File.WriteAllText(AppSettingsPath, json, Encoding.UTF8);
    }

    private static void TryDeleteBrokenConfig()
    {
        try
        {
            if (File.Exists(AppSettingsPath))
                File.Delete(AppSettingsPath);
        }
        catch { }
    }

    private static void TryOpenConfigForEdit()
    {
        try
        {
            if (!File.Exists(AppSettingsPath))
                return;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = AppSettingsPath,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        HandleFatalException(e.Exception, "ui");
        e.Handled = true;
        Shutdown(-1);
    }

    private void CurrentDomain_UnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception
                 ?? new Exception("Неизвестная критическая ошибка домена приложения.");
        HandleFatalException(ex, "appdomain");
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        HandleFatalException(e.Exception, "task");
        e.SetObserved();
    }

    private void HandleFatalException(Exception ex, string source)
    {
        if (Interlocked.Exchange(ref _fatalErrorHandled, 1) == 1)
            return;

        var backupSaved = TrySaveCrashBackup(out var backupPath, out var backupError);
        var backupText = backupSaved
            ? $"Текст сохранён в резервный файл:\n{backupPath}"
            : string.IsNullOrWhiteSpace(backupError)
                ? "Резервная копия текста не создана."
                : $"Не удалось создать резервную копию: {backupError}";

        MessageBox.Show(
            "Произошла непредвиденная ошибка. Приложение будет закрыто.\n\n" +
            backupText +
            "\n\nИсточник: " + source +
            "\n\n" + ex.Message,
            "Критическая ошибка",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private bool TrySaveCrashBackup(out string? backupPath, out string? error)
    {
        backupPath = null;
        error = null;

        try
        {
            if (Current?.MainWindow is MainWindow mainWindow)
                return mainWindow.TrySaveCrashBackup(out backupPath, out error);

            error = "Окно редактора недоступно.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static ServiceProvider ConfigureServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();

        services.AddOptions<AppSettings>()
            .Bind(configuration.GetSection("AppSettings"));

        // LlmSettingsProvider bridges LlmConfig → LlmSettings record
        services.AddSingleton<LlmSettingsProvider>();
        services.AddSingleton<Func<LlmSettings>>(sp =>
            () => sp.GetRequiredService<LlmSettingsProvider>().CurrentValue);

        services.AddSingleton<OpenAiLlmClient>();
        services.AddSingleton<FoundryLocalLlmClient>();
        services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
