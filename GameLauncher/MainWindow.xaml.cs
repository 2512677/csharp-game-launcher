using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameLauncher;

internal enum LauncherStatus
{
    Idle,
    Checking,
    NotInstalled,
    UpdateAvailable,
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
    private GameCardViewModel? _selectedCard;
    private GameUpdater? _updater;
    private Version? _onlineVersion;
    private LauncherStatus _status = LauncherStatus.Idle;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_ContentRendered(object sender, EventArgs e)
    {
        LoadLibrary();
    }

    private void LoadLibrary()
    {
        try
        {
            LauncherSettings settings = LauncherSettings.Load(
                Path.Combine(_rootPath, "launcher-settings.json"));

            Title = settings.LauncherName;
            LauncherTitleText.Text = settings.LauncherName;

            List<GameCardViewModel> games = settings.Games
                .Select(game => new GameCardViewModel(game, LoadImage(game.CoverImage)))
                .ToList();

            GamesList.ItemsSource = games;
            GameCountText.Text = FormatGameCount(games.Count);
            GamesList.SelectedIndex = 0;
        }
        catch (Exception exception)
        {
            LogError(exception);
            SetFailed(exception);
            MessageBox.Show(
                this,
                $"Не удалось загрузить библиотеку игр.\n\n{exception.Message}",
                "Ошибка настроек",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GamesList.SelectedItem is not GameCardViewModel card || card == _selectedCard)
        {
            return;
        }

        _selectedCard = card;
        _updater = new GameUpdater(HttpClient, card.Settings, _rootPath);
        _onlineVersion = null;

        SelectedGameNameText.Text = card.Name;
        SelectedGameDescriptionText.Text = string.IsNullOrWhiteSpace(card.Description)
            ? "Внутренняя игровая сборка"
            : card.Description;
        BackgroundImage.Source = LoadImage(card.Settings.BackgroundImage) ?? card.CoverImage;

        await CheckSelectedGameAsync();
    }

    private async Task CheckSelectedGameAsync()
    {
        if (_updater is null || _selectedCard is null || !TryBeginOperation())
        {
            return;
        }

        GameUpdater updater = _updater;
        GameCardViewModel card = _selectedCard;
        CancellationTokenSource operation = _operationCancellation!;
        var progress = new Progress<LauncherProgress>(ShowProgress);

        try
        {
            card.Status = "Проверка…";
            GameCheckResult result = await updater.CheckAsync(progress, operation.Token);
            _onlineVersion = result.OnlineVersion;

            if (!result.IsInstalled)
            {
                SetNotInstalled(result.OnlineVersion);
            }
            else if (result.UpdateAvailable)
            {
                SetUpdateAvailable(result.LocalVersion, result.OnlineVersion);
            }
            else
            {
                SetReady(result.LocalVersion, "Игра готова к запуску.");
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            StatusText.Text = "Операция отменена.";
        }
        catch (Exception exception)
        {
            LogError(exception);
            if (updater.IsGameInstalled)
            {
                SetReady(
                    updater.LocalVersion,
                    "Сервер обновлений недоступен. Можно играть офлайн.",
                    "Офлайн");
            }
            else
            {
                SetFailed(exception);
            }
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private async Task InstallSelectedGameAsync()
    {
        if (_updater is null
            || _selectedCard is null
            || _onlineVersion is null
            || !TryBeginOperation())
        {
            return;
        }

        GameUpdater updater = _updater;
        CancellationTokenSource operation = _operationCancellation!;
        var progress = new Progress<LauncherProgress>(ShowProgress);

        try
        {
            UpdateResult result = await updater.InstallOrUpdateAsync(
                _onlineVersion,
                progress,
                operation.Token);
            SetReady(result.Version, result.Message);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            StatusText.Text = "Операция отменена.";
        }
        catch (Exception exception)
        {
            LogError(exception);
            if (updater.IsGameInstalled)
            {
                SetReady(
                    updater.LocalVersion,
                    "Обновление не установлено. Можно запустить прежнюю версию.",
                    "Ошибка обновления");
            }
            else
            {
                SetFailed(exception);
                MessageBox.Show(
                    this,
                    $"Не удалось установить игру.\n\n{exception.Message}",
                    "Ошибка установки",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            EndOperation(operation);
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
            if (_selectedCard is not null)
            {
                _selectedCard.Status = $"Загрузка {percentage}%";
            }
        }
        else if (_selectedCard is not null)
        {
            _selectedCard.Status = progress.Phase switch
            {
                LauncherPhase.Checking => "Проверка…",
                LauncherPhase.Installing => "Установка…",
                _ => _selectedCard.Status
            };
        }
    }

    private void SetNotInstalled(Version onlineVersion)
    {
        _status = LauncherStatus.NotInstalled;
        StatusText.Text = "Игра ещё не установлена.";
        VersionText.Text = $"Доступная версия: {onlineVersion}";
        PlayButton.Content = "Установить";
        HideProgress();

        if (_selectedCard is not null)
        {
            _selectedCard.Status = "Не установлена";
        }
    }

    private void SetUpdateAvailable(Version? localVersion, Version onlineVersion)
    {
        _status = LauncherStatus.UpdateAvailable;
        StatusText.Text = "Доступно обновление.";
        VersionText.Text = $"Установлена: {localVersion}  •  Доступна: {onlineVersion}";
        PlayButton.Content = "Обновить";
        HideProgress();

        if (_selectedCard is not null)
        {
            _selectedCard.Status = $"Обновление {onlineVersion}";
        }
    }

    private void SetReady(Version? version, string message, string cardStatus = "Готова")
    {
        _status = LauncherStatus.Ready;
        StatusText.Text = message;
        VersionText.Text = version is null ? "Версия: неизвестна" : $"Версия: {version}";
        PlayButton.Content = "Играть";
        HideProgress();

        if (_selectedCard is not null)
        {
            _selectedCard.Status = cardStatus;
        }
    }

    private void SetFailed(Exception exception)
    {
        _status = LauncherStatus.Failed;
        StatusText.Text = $"Ошибка: {exception.Message}";
        VersionText.Text = "Версия: —";
        PlayButton.Content = "Повторить";
        HideProgress();

        if (_selectedCard is not null)
        {
            _selectedCard.Status = "Ошибка";
        }
    }

    private void HideProgress()
    {
        ProgressBar.Visibility = Visibility.Collapsed;
        ProgressBar.IsIndeterminate = false;
    }

    private bool TryBeginOperation()
    {
        if (_operationCancellation is not null)
        {
            return false;
        }

        _operationCancellation = new CancellationTokenSource();
        GamesList.IsEnabled = false;
        PlayButton.IsEnabled = false;
        return true;
    }

    private void EndOperation(CancellationTokenSource operation)
    {
        if (!ReferenceEquals(_operationCancellation, operation))
        {
            return;
        }

        operation.Dispose();
        _operationCancellation = null;
        GamesList.IsEnabled = true;
        PlayButton.IsEnabled = _status is LauncherStatus.NotInstalled
            or LauncherStatus.UpdateAvailable
            or LauncherStatus.Ready
            or LauncherStatus.Failed;
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_status)
        {
            case LauncherStatus.NotInstalled:
            case LauncherStatus.UpdateAvailable:
                await InstallSelectedGameAsync();
                break;
            case LauncherStatus.Ready:
                LaunchSelectedGame();
                break;
            case LauncherStatus.Failed:
                await CheckSelectedGameAsync();
                break;
        }
    }

    private void LaunchSelectedGame()
    {
        if (_updater is null)
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
            StatusText.Text = "Игра запущена.";
            WindowState = WindowState.Minimized;
        }
        catch (Exception exception)
        {
            LogError(exception);
            SetFailed(exception);
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

    private ImageSource? LoadImage(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        string imagePath = ResolveInsideLauncherDirectory(relativePath);
        if (!File.Exists(imagePath))
        {
            return null;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(imagePath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private string ResolveInsideLauncherDirectory(string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(_rootPath)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Путь к изображению выходит за пределы папки лаунчера.");
        }

        return fullPath;
    }

    private void LogError(Exception exception)
    {
        try
        {
            string gameId = _selectedCard?.Settings.Id ?? "launcher";
            string logEntry = $"[{DateTimeOffset.Now:O}] [{gameId}] {exception}\n\n";
            File.AppendAllText(Path.Combine(_rootPath, "launcher.log"), logEntry);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string FormatGameCount(int count)
    {
        int lastTwoDigits = count % 100;
        int lastDigit = count % 10;
        string word = lastTwoDigits is >= 11 and <= 14
            ? "игр"
            : lastDigit switch
            {
                1 => "игра",
                2 or 3 or 4 => "игры",
                _ => "игр"
            };

        return $"{count} {word}";
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

internal sealed class GameCardViewModel : INotifyPropertyChanged
{
    private string _status = "Не проверено";

    public GameCardViewModel(GameSettings settings, ImageSource? coverImage)
    {
        Settings = settings;
        CoverImage = coverImage;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public GameSettings Settings { get; }
    public string Name => Settings.Name;
    public string Description => Settings.Description;
    public ImageSource? CoverImage { get; }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }
}
