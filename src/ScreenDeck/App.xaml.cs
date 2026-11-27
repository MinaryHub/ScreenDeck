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
            DispatcherUnhandledException += (_, args) => { args.Handled = true; OnUiError(args.Exception); };
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                Log(args.ExceptionObject as Exception);
                Report(args.ExceptionObject as Exception);
            };
            TaskScheduler.UnobservedTaskException += (_, args) => { Log(args.Exception); args.SetObserved(); };
            base.OnStartup(e);
        }

        private const int MaxErrors = 3;                                  // this many within ErrorWindow → give up
        private static readonly TimeSpan ErrorWindow = TimeSpan.FromSeconds(30);
        private readonly System.Collections.Generic.Queue<DateTime> _recentErrors = new();
        private bool _reporting;

        /// <summary>
        /// A single failure (one refresh, one move) shouldn't end the app: log it, tell the user once, carry on.
        /// Errors that keep coming back mean the state is broken; then report and shut down as before.
        /// </summary>
        private void OnUiError(Exception ex)
        {
            Log(ex);
            var now = DateTime.UtcNow;
            _recentErrors.Enqueue(now);
            while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > ErrorWindow) _recentErrors.Dequeue();

            if (_recentErrors.Count >= MaxErrors)
            {
                if (_reporting) return;
                _reporting = true;
                Report(ex);
                Shutdown(1);
                return;
            }
            if (_reporting) return;   // a message box is already open (timers keep running behind it)
            _reporting = true;
            try
            {
                MessageBox.Show($"ScreenDeck ran into an error and skipped that step.\n\n{ex.Message}\n\nLog: {LogPath}",
                    "ScreenDeck", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _reporting = false; }
        }

        private static void Report(Exception? ex)
        {
            MessageBox.Show($"ScreenDeck ran into an error.\n\n{ex?.Message}\n\nLog: {LogPath}",
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
