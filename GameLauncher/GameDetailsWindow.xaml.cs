using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

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

public partial class GameDetailsWindow : Window
{
    private const string YoutubePlayerHost = "player.sherdorgames.local";

    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly LauncherSettings _launcherSettings;
    private readonly string _settingsPath;
    private readonly GameSettings _game;
    private readonly string _rootPath;
    private readonly GameUpdater _updater;
    private readonly bool _installWhenMissing;
    private readonly List<ImageSource> _slideshowImages = [];
    private readonly DispatcherTimer _slideshowTimer;
    private CancellationTokenSource? _operationCancellation;
    private Version? _onlineVersion;
    private LauncherStatus _status = LauncherStatus.Idle;
    private int _currentSlideIndex;
    private bool _youtubeEventsConfigured;

    internal GameDetailsWindow(
        LauncherSettings launcherSettings,
        string settingsPath,
        GameSettings game,
        string rootPath,
        bool installWhenMissing = false)
    {
        InitializeComponent();
        _launcherSettings = launcherSettings;
        _settingsPath = settingsPath;
        _game = game;
        _rootPath = Path.GetFullPath(rootPath);
        _updater = new GameUpdater(HttpClient, game, rootPath);
        _installWhenMissing = installWhenMissing;

#if !DEBUG
        EditGameButton.Visibility = Visibility.Collapsed;
#endif

        Title = game.Name;
        GameNameText.Text = game.Name;
        DescriptionText.Text = string.IsNullOrWhiteSpace(game.Description)
            ? "Описание не указано."
            : game.Description;
        LogoImage.Source = LoadImage(game.LogoImage);
        BackgroundImage.Source = LoadImage(game.BackgroundImage ?? game.CoverImage);

        _slideshowTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _slideshowTimer.Tick += SlideshowTimer_Tick;
        InitializeSlideshow();
    }

    internal GameSettings? EditedGame { get; private set; }

    private async void Window_ContentRendered(object sender, EventArgs e)
    {
        _ = InitializeYoutubePlayerAsync();

        if (_slideshowImages.Count > 1)
        {
            _slideshowTimer.Start();
        }

        await CheckAndUpdateAsync();
    }

    private async Task InitializeYoutubePlayerAsync()
    {
        if (_game.YoutubeVideoId is null)
        {
            return;
        }

        VideoPanel.Visibility = Visibility.Visible;
        try
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SherdorGamesLauncher",
                "WebView2");
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: userDataFolder);
            await YoutubePlayer.EnsureCoreWebView2Async(environment);

            string webAssetsPath = Path.Combine(_rootPath, "web");
            YoutubePlayer.CoreWebView2.SetVirtualHostNameToFolderMapping(
                YoutubePlayerHost,
                webAssetsPath,
                CoreWebView2HostResourceAccessKind.DenyCors);

            YoutubePlayer.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            YoutubePlayer.CoreWebView2.Settings.AreDevToolsEnabled = false;
            YoutubePlayer.CoreWebView2.Settings.IsStatusBarEnabled = false;
            YoutubePlayer.CoreWebView2.Settings.IsZoomControlEnabled = false;

            if (!_youtubeEventsConfigured)
            {
                YoutubePlayer.CoreWebView2.NewWindowRequested += (_, args) =>
                {
                    args.Handled = true;
                };
                YoutubePlayer.CoreWebView2.NavigationStarting += (_, args) =>
                {
                    if (IsFullYoutubePage(args.Uri))
                    {
                        args.Cancel = true;
                    }
                };
                YoutubePlayer.CoreWebView2.NavigationCompleted += (_, args) =>
                {
                    if (!args.IsSuccess)
                    {
                        ShowYoutubeFallback();
                    }
                };
                _youtubeEventsConfigured = true;
            }

            VideoFallbackPanel.Visibility = Visibility.Collapsed;
            YoutubePlayer.Visibility = Visibility.Visible;
            string playerUrl = $"https://{YoutubePlayerHost}/youtube-player.html"
                + $"?video={Uri.EscapeDataString(_game.YoutubeVideoId)}";
            YoutubePlayer.CoreWebView2.Navigate(playerUrl);
        }
        catch (Exception exception)
        {
            LogError(exception);
            ShowYoutubeFallback();
        }
    }

    private void ShowYoutubeFallback()
    {
        YoutubePlayer.Visibility = Visibility.Collapsed;
        VideoFallbackPanel.Visibility = Visibility.Visible;
    }

    private async void RetryYoutubeButton_Click(object sender, RoutedEventArgs e)
    {
        await InitializeYoutubePlayerAsync();
    }

    private static bool IsFullYoutubePage(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string host = uri.Host.ToLowerInvariant();
        return (host.Equals("youtube.com", StringComparison.Ordinal)
                || host.EndsWith(".youtube.com", StringComparison.Ordinal))
            && (uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase)
                || uri.AbsolutePath.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase));
    }

    private void InitializeSlideshow()
    {
        IEnumerable<string?> imagePaths = _game.SlideshowImages.Count > 0
            ? _game.SlideshowImages
            : [_game.CoverImage ?? _game.BackgroundImage];

        foreach (string? imagePath in imagePaths)
        {
            ImageSource? image = LoadImage(imagePath);
            if (image is not null)
            {
                _slideshowImages.Add(image);
            }
        }

        if (_slideshowImages.Count == 0)
        {
            return;
        }

        ShowSlide(0, animate: false);
        SlideshowControls.Visibility = _slideshowImages.Count > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ShowSlide(int index, bool animate = true)
    {
        if (_slideshowImages.Count == 0)
        {
            return;
        }

        _currentSlideIndex = (index + _slideshowImages.Count) % _slideshowImages.Count;
        CoverImage.Source = _slideshowImages[_currentSlideIndex];
        SlideCounterText.Text = $"{_currentSlideIndex + 1} / {_slideshowImages.Count}";

        CoverImage.BeginAnimation(OpacityProperty, null);
        if (animate)
        {
            CoverImage.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(280)));
        }
        else
        {
            CoverImage.Opacity = 1;
        }
    }

    private void PreviousSlideButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSlide(_currentSlideIndex - 1);
        RestartSlideshowTimer();
    }

    private void NextSlideButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSlide(_currentSlideIndex + 1);
        RestartSlideshowTimer();
    }

    private void SlideshowTimer_Tick(object? sender, EventArgs e)
    {
        ShowSlide(_currentSlideIndex + 1);
    }

    private void RestartSlideshowTimer()
    {
        if (_slideshowImages.Count <= 1)
        {
            return;
        }

        _slideshowTimer.Stop();
        _slideshowTimer.Start();
    }

    private async Task CheckAndUpdateAsync()
    {
        if (!TryBeginOperation())
        {
            return;
        }

        CancellationTokenSource operation = _operationCancellation!;
        var progress = new Progress<LauncherProgress>(ShowProgress);

        try
        {
            GameCheckResult result = await _updater.CheckAsync(progress, operation.Token);
            _onlineVersion = result.OnlineVersion;

            if (!result.IsInstalled)
            {
                if (!_installWhenMissing)
                {
                    SetNotInstalled(result.OnlineVersion);
                    return;
                }

                StatusText.Text = $"Начинаем установку версии {result.OnlineVersion}…";
                UpdateResult installation = await _updater.InstallOrUpdateAsync(
                    result.OnlineVersion,
                    progress,
                    operation.Token);
                SetReady(installation.Version, "Игра установлена. Можно играть.");
                return;
            }

            if (result.UpdateAvailable)
            {
                SetUpdateAvailable(result.LocalVersion, result.OnlineVersion);
                return;
            }

            SetReady(result.LocalVersion, "Игра готова к запуску.");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            StatusText.Text = "Операция отменена.";
        }
        catch (Exception exception)
        {
            LogError(exception);
            if (_updater.IsGameInstalled)
            {
                SetReady(
                    _updater.LocalVersion,
                    "Не удалось проверить обновление. Можно играть офлайн.");
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

    private async Task InstallGameAsync()
    {
        if (_onlineVersion is null || !TryBeginOperation())
        {
            return;
        }

        CancellationTokenSource operation = _operationCancellation!;
        var progress = new Progress<LauncherProgress>(ShowProgress);

        try
        {
            UpdateResult result = await _updater.InstallOrUpdateAsync(
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
            if (_updater.IsGameInstalled && _onlineVersion is not null)
            {
                SetUpdateAvailable(_updater.LocalVersion, _onlineVersion);
                StatusText.Text = "Не удалось установить обновление. Текущую версию можно запустить.";
            }
            else
            {
                SetFailed(exception);
            }

            MessageBox.Show(
                this,
                $"Не удалось установить игру.\n\n{exception.Message}",
                "Ошибка установки",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
        ActionButton.Content = progress.Phase switch
        {
            LauncherPhase.Checking => "Проверка…",
            LauncherPhase.Downloading => "Загрузка…",
            LauncherPhase.Installing => "Установка…",
            _ => ActionButton.Content
        };

        if (UpdateButton.Visibility == Visibility.Visible)
        {
            UpdateButton.Content = progress.Phase switch
            {
                LauncherPhase.Downloading => "Загрузка…",
                LauncherPhase.Installing => "Установка…",
                _ => UpdateButton.Content
            };
        }

        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = progress.Percentage is null;
        if (progress.Percentage is int percentage)
        {
            ProgressBar.Value = percentage;
        }
    }

    private void SetNotInstalled(Version onlineVersion)
    {
        _status = LauncherStatus.NotInstalled;
        StatusText.Text = "Игра ещё не установлена.";
        VersionText.Text = $"Доступная версия: {onlineVersion}";
        ActionButton.Content = "Установить";
        UpdateButton.Visibility = Visibility.Collapsed;
        HideProgress();
    }

    private void SetUpdateAvailable(Version? localVersion, Version onlineVersion)
    {
        _status = LauncherStatus.UpdateAvailable;
        StatusText.Text = "Доступно обновление. Можно играть сейчас или обновить игру.";
        VersionText.Text = localVersion is null
            ? $"Установленная версия неизвестна · доступна {onlineVersion}"
            : $"Установлена: {localVersion} · доступна: {onlineVersion}";
        ActionButton.Content = "Играть";
        UpdateButton.Content = "Обновить";
        UpdateButton.Visibility = Visibility.Visible;
        HideProgress();
    }

    private void SetReady(Version? version, string message)
    {
        _status = LauncherStatus.Ready;
        StatusText.Text = message;
        VersionText.Text = version is null ? "Версия: неизвестна" : $"Версия: {version}";
        ActionButton.Content = "Играть";
        UpdateButton.Visibility = Visibility.Collapsed;
        HideProgress();
    }

    private void SetFailed(Exception exception)
    {
        _status = LauncherStatus.Failed;
        StatusText.Text = $"Ошибка: {exception.Message}";
        VersionText.Text = "Версия: —";
        ActionButton.Content = "Повторить";
        UpdateButton.Visibility = Visibility.Collapsed;
        HideProgress();
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
        ActionButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;
        EditGameButton.IsEnabled = false;
        return true;
    }

    private void EndOperation(CancellationTokenSource operation)
    {
        if (!ReferenceEquals(operation, _operationCancellation))
        {
            return;
        }

        operation.Dispose();
        _operationCancellation = null;
        ActionButton.IsEnabled = _status is LauncherStatus.NotInstalled
            or LauncherStatus.UpdateAvailable
            or LauncherStatus.Ready
            or LauncherStatus.Failed;
        UpdateButton.IsEnabled = _status == LauncherStatus.UpdateAvailable;
        EditGameButton.IsEnabled = true;
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_status)
        {
            case LauncherStatus.NotInstalled:
                await InstallGameAsync();
                break;
            case LauncherStatus.UpdateAvailable:
            case LauncherStatus.Ready:
                LaunchGame();
                break;
            case LauncherStatus.Failed:
                await CheckAndUpdateAsync();
                break;
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_status == LauncherStatus.UpdateAvailable)
        {
            await InstallGameAsync();
        }
    }

    private void EditGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCancellation is not null)
        {
            return;
        }

        _slideshowTimer.Stop();
        var dialog = new AddGameWindow(
            _launcherSettings,
            _settingsPath,
            _rootPath,
            _game)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true && dialog.CreatedGame is not null)
        {
            EditedGame = dialog.CreatedGame;
            Close();
            return;
        }

        if (_slideshowImages.Count > 1)
        {
            _slideshowTimer.Start();
        }
    }

    private void LaunchGame()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _updater.GameExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(_updater.GameExecutablePath)!,
                UseShellExecute = true
            });
            Close();
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

    private ImageSource? LoadImage(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        string path = ResolveInsideRoot(relativePath);
        if (!File.Exists(path))
        {
            return null;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private string ResolveInsideRoot(string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(_rootPath)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Путь к изображению выходит за пределы лаунчера.");
        }

        return fullPath;
    }

    private void LogError(Exception exception)
    {
        try
        {
            string logEntry = $"[{DateTimeOffset.Now:O}] [{_game.Id}] {exception}\n\n";
            File.AppendAllText(Path.Combine(_rootPath, "launcher.log"), logEntry);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _slideshowTimer.Stop();
        _operationCancellation?.Cancel();
        YoutubePlayer.Dispose();
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
