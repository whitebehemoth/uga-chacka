using HomographResolver;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Text;
using System.Windows;

namespace uga_chacka
{
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
            AppContext.BaseDirectory,
            "appsettings.json");

        public static IConfigurationRoot Configuration { get; } = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(AppSettingsPath, optional: true, reloadOnChange: true)
            .AddUserSecrets<App>(optional: true)
            .Build();

        private static ServiceProvider ConfigureServices()
        {
            var services = new ServiceCollection();

            // Register AppSettings for UI binding
            services.AddOptions<AppSettings>()
                .Bind(Configuration.GetSection("AppSettings"));

            // Register LlmSettings separately for HomographResolver library
            services.AddOptions<LlmSettings>()
                .Bind(Configuration.GetSection("AppSettings:Llm"));

            services.AddSingleton<OpenAiLlmClient>();
            services.AddSingleton<FoundryLocalLlmClient>();
            services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
            services.AddSingleton<MainWindow>();

            return services.BuildServiceProvider();
        }
    }
}

