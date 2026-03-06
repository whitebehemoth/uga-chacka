using Microsoft.Extensions.Configuration;
using System.IO;
using System.Text;
using System.Windows;

namespace uga_chacka
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            base.OnStartup(e);
        }
        public static string AppSettingsPath { get; } = Path.Combine(
            AppContext.BaseDirectory,
            "appsettings.json");

        public static IConfigurationRoot Configuration { get; } = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(AppSettingsPath, optional: true, reloadOnChange: true)
            .AddUserSecrets<App>(optional: true)
            .Build();
    }
}

