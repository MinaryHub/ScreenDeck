using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace ScreenDeck
{
    public partial class App : Application
    {
        private static string LogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenDeck", "crash.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            // Log unhandled errors and tell the user instead of dying silently.
            DispatcherUnhandledException += (_, args) => { Report(args.Exception); args.Handled = true; Shutdown(1); };
            AppDomain.CurrentDomain.UnhandledException += (_, args) => Report(args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, args) => { Log(args.Exception); args.SetObserved(); };
            base.OnStartup(e);
        }

        private static void Report(Exception? ex)
        {
            Log(ex);
            MessageBox.Show($"ScreenDeck에 오류가 발생했습니다.\n\n{ex?.Message}\n\n로그: {LogPath}",
                "ScreenDeck", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private static void Log(Exception? ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var version = typeof(App).Assembly.GetName().Version;
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{version} {Environment.OSVersion}\n{ex}\n\n");
            }
            catch { }
        }
    }
}
