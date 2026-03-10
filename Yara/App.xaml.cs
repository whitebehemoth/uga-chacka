using System.IO;
using System.Text;
using System.Windows;
using HomographResolver;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WhiteBehemoth.Yara.Services;
using WhiteBehemoth.Yara.Settings;

namespace WhiteBehemoth.Yara;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Services ??= ConfigureServices();

        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Services is IDisposable disposable)
            disposable.Dispose();
        base.OnExit(e);
    }

    public static string AppSettingsPath { get; } = Path.Combine(
        AppContext.BaseDirectory, "appsettings.json");

    public static IConfigurationRoot Configuration { get; } = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile(AppSettingsPath, optional: true, reloadOnChange: true)
        .AddUserSecrets<App>(optional: true)
        .Build();

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddOptions<AppSettings>()
            .Bind(Configuration.GetSection("AppSettings"));

        // Bridge new LlmConfig → existing LlmSettings for HomographResolver clients
        services.AddSingleton<LlmSettingsProvider>();
        services.AddSingleton<IOptionsMonitor<LlmSettings>>(sp =>
            sp.GetRequiredService<LlmSettingsProvider>());

        services.AddSingleton<OpenAiLlmClient>();
        services.AddSingleton<FoundryLocalLlmClient>();
        services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }
}
