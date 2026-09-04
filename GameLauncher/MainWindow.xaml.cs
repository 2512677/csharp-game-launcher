using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Media.Imaging;

namespace GameLauncher;

internal enum LauncherStatus
{
    Checking,
    Downloading,
    Installing,
    Ready,
    Failed
}

public partial class MainWindow : Window
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly string _rootPath = AppContext.BaseDirectory;
    private CancellationTokenSource? _operationCancellation;
    private GameUpdater? _updater;
    private LauncherStatus _status = LauncherStatus.Checking;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_ContentRendered(object sender, EventArgs e)
    {
        await CheckForUpdatesAsync();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_operationCancellation is not null)
        {
            return;
        }

        _operationCancellation = new CancellationTokenSource();
        PlayButton.IsEnabled = false;

        try
        {
            LauncherSettings settings = LauncherSettings.Load(
                Path.Combine(_rootPath, "launcher-settings.json"));

            Title = $"{settings.GameName} — Лаунчер";
            GameTitleText.Text = settings.GameName;
            ApplyBranding(settings);

            _updater = new GameUpdater(HttpClient, settings, _rootPath);
            var progress = new Progress<LauncherProgress>(ShowProgress);
            UpdateResult result = await _updater.CheckAndUpdateAsync(
                progress,
                _operationCancellation.Token);

            SetReady(result.Version, result.Message);
        }
        catch (OperationCanceledException) when (_operationCancellation.IsCancellationRequested)
        {
            StatusText.Text = "Операция отменена.";
        }
        catch (Exception exception)
        {
            LogError(exception);

            if (_updater?.IsGameInstalled == true)
            {
                SetReady(
                    _updater.LocalVersion,
                    "Сервер обновлений недоступен. Можно играть в установленную версию.");
            }
            else
            {
                SetFailed(exception);
            }
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            PlayButton.IsEnabled = _status is LauncherStatus.Ready or LauncherStatus.Failed;
        }
    }

    private void ShowProgress(LauncherProgress progress)
    {
        _status = progress.Phase switch
        {
            LauncherPhase.Checking => LauncherStatus.Checking,
            LauncherPhase.Downloading => LauncherStatus.Downloading,
            LauncherPhase.Installing => LauncherStatus.Installing,
            _ => _status
        };

        StatusText.Text = progress.Message;
        PlayButton.Content = progress.Phase switch
        {
            LauncherPhase.Checking => "Проверка…",
            LauncherPhase.Downloading => "Загрузка…",
            LauncherPhase.Installing => "Установка…",
            _ => PlayButton.Content
        };

        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = progress.Percentage is null;
        if (progress.Percentage is int percentage)
        {
            ProgressBar.Value = percentage;
        }
    }

    private void SetReady(Version? version, string message)
    {
        _status = LauncherStatus.Ready;
        VersionText.Text = version is null ? "Версия: неизвестна" : $"Версия: {version}";
        StatusText.Text = message;
        ProgressBar.Visibility = Visibility.Collapsed;
        ProgressBar.IsIndeterminate = false;
        PlayButton.Content = "Играть";
    }

    private void SetFailed(Exception exception)
    {
        _status = LauncherStatus.Failed;
        StatusText.Text = "Не удалось установить игру. Проверьте подключение и повторите попытку.";
        ProgressBar.Visibility = Visibility.Collapsed;
        ProgressBar.IsIndeterminate = false;
        PlayButton.Content = "Повторить";

        MessageBox.Show(
            this,
            $"Не удалось подготовить игру.\n\n{exception.Message}",
            "Ошибка лаунчера",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_status == LauncherStatus.Failed)
        {
            await CheckForUpdatesAsync();
            return;
        }

        if (_status != LauncherStatus.Ready || _updater is null)
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _updater.GameExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(_updater.GameExecutablePath)!,
                UseShellExecute = true
            };

            Process.Start(startInfo);
            Close();
        }
        catch (Exception exception)
        {
            LogError(exception);
            StatusText.Text = "Не удалось запустить игру.";
            MessageBox.Show(
                this,
                $"Не удалось запустить игру.\n\n{exception.Message}",
                "Ошибка запуска",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _operationCancellation?.Cancel();
    }

    private void LogError(Exception exception)
    {
        try
        {
            string logEntry = $"[{DateTimeOffset.Now:O}] {exception}\n\n";
            File.AppendAllText(Path.Combine(_rootPath, "launcher.log"), logEntry);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ApplyBranding(LauncherSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BackgroundImage))
        {
            BackgroundImage.Source = null;
            return;
        }

        string imagePath = ResolveInsideLauncherDirectory(settings.BackgroundImage);
        if (!File.Exists(imagePath))
        {
            throw new InvalidOperationException(
                $"Не найден файл фона '{settings.BackgroundImage}'.");
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(imagePath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        BackgroundImage.Source = image;
    }

    private string ResolveInsideLauncherDirectory(string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(_rootPath)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Путь к оформлению выходит за пределы папки лаунчера.");
        }

        return fullPath;
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}
