using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameLauncher;

public partial class MainWindow : Window
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private readonly string _rootPath = Path.GetFullPath(AppContext.BaseDirectory);
    private readonly string _settingsPath;
    private LauncherSettings? _settings;
    private bool _gameWindowIsOpen;

    public MainWindow()
    {
        InitializeComponent();
        _settingsPath = Path.Combine(_rootPath, "launcher-settings.json");

#if !DEBUG
        AddGameButton.Visibility = Visibility.Collapsed;
#endif
    }

    private void Window_ContentRendered(object sender, EventArgs e)
    {
        LoadLibrary();
    }

    private void LoadLibrary()
    {
        try
        {
            _settings = LauncherSettings.Load(_settingsPath);
            Title = _settings.LauncherName;
            LauncherTitleText.Text = _settings.LauncherName;

            List<GameCardViewModel> cards = _settings.Games
                .Select(CreateCard)
                .ToList();

            GamesList.ItemsSource = cards;
            GameCountText.Text = FormatGameCount(cards.Count);
            EmptyLibraryPanel.Visibility = cards.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            BackgroundImage.Source = cards.FirstOrDefault()?.BackgroundImage;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"Не удалось загрузить библиотеку игр.\n\n{exception.Message}",
                "Ошибка лаунчера",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private GameCardViewModel CreateCard(GameSettings game)
    {
        var updater = new GameUpdater(HttpClient, game, _rootPath);
        string status;

        if (!updater.IsGameInstalled)
        {
            status = "Не установлена";
        }
        else if (updater.LocalVersion is Version version)
        {
            status = $"Установлена · {version}";
        }
        else
        {
            status = "Установлена";
        }

        return new GameCardViewModel(
            game,
            LoadImage(game.CoverImage ?? game.LogoImage),
            LoadImage(game.BackgroundImage ?? game.CoverImage),
            status);
    }

    private void AddGameButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            LoadLibrary();
        }

        if (_settings is null)
        {
            return;
        }

        var dialog = new AddGameWindow(_settings, _settingsPath, _rootPath)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            LoadLibrary();

            if (dialog.CreatedGame is not null)
            {
                OpenGame(dialog.CreatedGame, installWhenMissing: true);
            }
        }
    }

    private void GamesList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_gameWindowIsOpen || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(GamesList, source) is not ListBoxItem item
            || item.DataContext is not GameCardViewModel card)
        {
            return;
        }

        GamesList.SelectedItem = card;
        OpenGame(card.Settings, installWhenMissing: false);
    }

    private void OpenGame(GameSettings game, bool installWhenMissing)
    {
        if (_gameWindowIsOpen || _settings is null)
        {
            return;
        }

        GameSettings? editedGame = null;
        _gameWindowIsOpen = true;
        try
        {
            var window = new GameDetailsWindow(
                _settings,
                _settingsPath,
                game,
                _rootPath,
                installWhenMissing)
            {
                Owner = this
            };
            window.ShowDialog();
            editedGame = window.EditedGame;
        }
        finally
        {
            _gameWindowIsOpen = false;
            LoadLibrary();
        }

        if (editedGame is not null && _settings is not null)
        {
            GameSettings reloadedGame = _settings.Games.FirstOrDefault(candidate =>
                candidate.Id.Equals(editedGame.Id, StringComparison.OrdinalIgnoreCase))
                ?? editedGame;
            OpenGame(reloadedGame, installWhenMissing: false);
        }
    }

    private ImageSource? LoadImage(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
            string rootWithSeparator = Path.TrimEndingDirectorySeparator(_rootPath)
                + Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(fullPath))
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(fullPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string FormatGameCount(int count)
    {
        int lastTwoDigits = count % 100;
        int lastDigit = count % 10;

        string noun = lastTwoDigits is >= 11 and <= 14
            ? "игр"
            : lastDigit switch
            {
                1 => "игра",
                2 or 3 or 4 => "игры",
                _ => "игр"
            };

        return $"{count} {noun}";
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

internal sealed record GameCardViewModel(
    GameSettings Settings,
    ImageSource? CoverImage,
    ImageSource? BackgroundImage,
    string Status)
{
    public string Name => Settings.Name;

    public string Description => string.IsNullOrWhiteSpace(Settings.Description)
        ? "Без описания"
        : Settings.Description;
}
