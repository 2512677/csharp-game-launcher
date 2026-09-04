using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace GameLauncher;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            CreateSingleInstanceMutexName(),
            out bool isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance;

        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Лаунчер уже запущен.",
                "Игровой лаунчер",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static string CreateSingleInstanceMutexName()
    {
        string launcherPath = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(launcherPath));
        return $@"Local\GameLauncher.{Convert.ToHexString(hash.AsSpan(0, 8))}";
    }
}
