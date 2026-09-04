using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace GameLauncher;

public partial class AddGameWindow : Window
{
    private readonly LauncherSettings _settings;
    private readonly string _settingsPath;
    private readonly string _rootPath;
    private readonly List<string> _existingSlideshowPaths = [];
    private readonly List<string> _newSlideshowSourcePaths = [];
    private readonly GameSettings? _editingGame;
    private string? _logoSourcePath;
    private string? _coverSourcePath;

    internal AddGameWindow(
        LauncherSettings settings,
        string settingsPath,
        string rootPath)
    {
        InitializeComponent();
        _settings = settings;
        _settingsPath = settingsPath;
        _rootPath = Path.GetFullPath(rootPath);
    }

    internal AddGameWindow(
        LauncherSettings settings,
        string settingsPath,
        string rootPath,
        GameSettings editingGame)
        : this(settings, settingsPath, rootPath)
    {
        _editingGame = editingGame;
        ConfigureEditMode();
    }

    internal GameSettings? CreatedGame { get; private set; }

    private void ConfigureEditMode()
    {
        if (_editingGame is null)
        {
            return;
        }

        Title = "Редактировать игру";
        HeaderTitleText.Text = "Редактировать игру";
        HeaderDescriptionText.Text =
            "Измените данные или добавьте изображения в слайд-шоу.";
        SaveButton.Content = "Сохранить";

        GameNameTextBox.Text = _editingGame.Name;
        DescriptionTextBox.Text = _editingGame.Description;
        GameDirectoryTextBox.Text = _editingGame.GameDirectory;
        GameExecutableTextBox.Text = _editingGame.GameExecutable;
        VersionUrlTextBox.Text = _editingGame.VersionUrl;
        PackageUrlTextBox.Text = _editingGame.PackageUrl;
        YoutubeUrlTextBox.Text = _editingGame.YoutubeUrl ?? string.Empty;

        SetExistingImagePreview(
            _editingGame.LogoImage,
            LogoPathText,
            LogoPreviewImage);
        SetExistingImagePreview(
            _editingGame.CoverImage,
            CoverPathText,
            CoverPreviewImage);

        _existingSlideshowPaths.AddRange(_editingGame.SlideshowImages);
        UpdateSlideshowPreview();
    }

    private void SetExistingImagePreview(
        string? relativePath,
        System.Windows.Controls.TextBlock pathText,
        System.Windows.Controls.Image preview)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        pathText.Text = Path.GetFileName(relativePath);
        preview.Source = TryLoadPreview(ResolveInsideRoot(relativePath));
    }

    private void BrowseLogo_Click(object sender, RoutedEventArgs e)
    {
        string? path = SelectImageFile();
        if (path is null)
        {
            return;
        }

        _logoSourcePath = path;
        LogoPathText.Text = Path.GetFileName(path);
        LogoPreviewImage.Source = LoadPreview(path);
    }

    private void BrowseCover_Click(object sender, RoutedEventArgs e)
    {
        string? path = SelectImageFile();
        if (path is null)
        {
            return;
        }

        _coverSourcePath = path;
        CoverPathText.Text = Path.GetFileName(path);
        CoverPreviewImage.Source = LoadPreview(path);
    }

    private void BrowseSlideshow_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string>? paths = SelectImageFiles();
        if (paths is null)
        {
            return;
        }

        foreach (string path in paths)
        {
            if (_newSlideshowSourcePaths.All(existing => !Path.GetFullPath(existing).Equals(
                    Path.GetFullPath(path),
                    StringComparison.OrdinalIgnoreCase)))
            {
                _newSlideshowSourcePaths.Add(path);
            }
        }

        UpdateSlideshowPreview();
    }

    private void ClearSlideshow_Click(object sender, RoutedEventArgs e)
    {
        _existingSlideshowPaths.Clear();
        _newSlideshowSourcePaths.Clear();
        UpdateSlideshowPreview();
    }

    private void UpdateSlideshowPreview()
    {
        int count = _existingSlideshowPaths.Count + _newSlideshowSourcePaths.Count;
        SlideshowCountText.Text = count == 0
            ? "Изображения не выбраны"
            : $"Выбрано изображений: {count}";

        string? previewPath = _newSlideshowSourcePaths.FirstOrDefault();
        if (previewPath is null && _existingSlideshowPaths.FirstOrDefault() is string existingPath)
        {
            previewPath = ResolveInsideRoot(existingPath);
        }

        SlideshowPreviewImage.Source = previewPath is null
            ? null
            : TryLoadPreview(previewPath);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;

        try
        {
            string name = RequireValue(GameNameTextBox.Text, "Введите название игры.");
            string description = DescriptionTextBox.Text.Trim();
            string directoryInput = RequireValue(
                GameDirectoryTextBox.Text,
                "Введите имя папки игры.");
            string executable = RequireValue(
                GameExecutableTextBox.Text,
                "Введите имя исполняемого файла игры.");
            string versionUrl = RequireValue(
                VersionUrlTextBox.Text,
                "Введите ссылку на Version.txt.");
            string packageUrl = RequireValue(
                PackageUrlTextBox.Text,
                "Введите ссылку на ZIP-архив игры.");
            string? youtubeUrl = string.IsNullOrWhiteSpace(YoutubeUrlTextBox.Text)
                ? null
                : YoutubeUrlTextBox.Text.Trim();

            string? existingLogo = _editingGame?.LogoImage;
            string? existingCover = _editingGame?.CoverImage;
            if (_logoSourcePath is null && string.IsNullOrWhiteSpace(existingLogo)
                || _coverSourcePath is null && string.IsNullOrWhiteSpace(existingCover))
            {
                throw new InvalidOperationException("Выберите логотип и обложку игры.");
            }

            string gameId = _editingGame?.Id ?? CreateUniqueGameId(name);
            string gameDirectory = NormalizeGameDirectory(directoryInput);
            string logoRelativePath = _logoSourcePath is null
                ? existingLogo!
                : CreateAssetRelativePath(
                    gameId,
                    _editingGame is null ? "logo" : $"logo-{Guid.NewGuid():N}",
                    _logoSourcePath);
            string coverRelativePath = _coverSourcePath is null
                ? existingCover!
                : CreateAssetRelativePath(
                    gameId,
                    _editingGame is null ? "cover" : $"cover-{Guid.NewGuid():N}",
                    _coverSourcePath);

            var slideshowRelativePaths = new List<string>(_existingSlideshowPaths);
            foreach (string sourcePath in _newSlideshowSourcePaths)
            {
                slideshowRelativePaths.Add(CreateAssetRelativePath(
                    gameId,
                    $"slides/slide-{Guid.NewGuid():N}",
                    sourcePath));
            }

            var game = new GameSettings
            {
                Id = gameId,
                Name = name,
                Description = description,
                GameDirectory = gameDirectory,
                GameExecutable = executable,
                VersionFile = _editingGame?.VersionFile ?? $"{gameId}.version.txt",
                LogoImage = logoRelativePath,
                CoverImage = coverRelativePath,
                BackgroundImage = coverRelativePath,
                SlideshowImages = slideshowRelativePaths,
                YoutubeUrl = youtubeUrl,
                VersionUrl = versionUrl,
                PackageUrl = packageUrl
            };
            game.Validate();

            SaveGame(game, logoRelativePath, coverRelativePath, slideshowRelativePaths);
            CreatedGame = game;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            ValidationText.Text = exception.Message;
        }
    }

    private void SaveGame(
        GameSettings game,
        string logoRelativePath,
        string coverRelativePath,
        IReadOnlyList<string> slideshowRelativePaths)
    {
        string assetDirectory = ResolveInsideRoot(Path.Combine("images", "games", game.Id));
        string installationDirectory = ResolveInsideRoot(game.GameDirectory);
        string? previousInstallationDirectory = _editingGame is null
            ? null
            : ResolveInsideRoot(_editingGame.GameDirectory);
        bool assetDirectoryCreated = !Directory.Exists(assetDirectory);
        bool installationDirectoryCreated = false;
        bool installationDirectoryMoved = false;
        bool settingsChanged = false;
        var copiedFiles = new List<string>();

        try
        {
            Directory.CreateDirectory(assetDirectory);

            if (_logoSourcePath is not null)
            {
                CopyImage(
                    _logoSourcePath,
                    ResolveInsideRoot(logoRelativePath),
                    copiedFiles);
            }

            if (_coverSourcePath is not null)
            {
                CopyImage(
                    _coverSourcePath,
                    ResolveInsideRoot(coverRelativePath),
                    copiedFiles);
            }

            int firstNewSlide = slideshowRelativePaths.Count - _newSlideshowSourcePaths.Count;
            for (int index = 0; index < _newSlideshowSourcePaths.Count; index++)
            {
                CopyImage(
                    _newSlideshowSourcePaths[index],
                    ResolveInsideRoot(slideshowRelativePaths[firstNewSlide + index]),
                    copiedFiles);
            }

            if (previousInstallationDirectory is not null
                && !previousInstallationDirectory.Equals(
                    installationDirectory,
                    StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(previousInstallationDirectory))
            {
                if (Directory.Exists(installationDirectory))
                {
                    if (Directory.EnumerateFileSystemEntries(installationDirectory).Any())
                    {
                        throw new InvalidOperationException(
                            "Новая папка игры уже существует и содержит файлы.");
                    }

                    Directory.Delete(installationDirectory);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(installationDirectory)!);
                Directory.Move(previousInstallationDirectory, installationDirectory);
                installationDirectoryMoved = true;
            }
            else if (!Directory.Exists(installationDirectory))
            {
                Directory.CreateDirectory(installationDirectory);
                installationDirectoryCreated = true;
            }

            if (_editingGame is null)
            {
                _settings.AddGame(game);
            }
            else
            {
                _settings.ReplaceGame(_editingGame, game);
            }

            settingsChanged = true;
            _settings.Save(_settingsPath);
        }
        catch
        {
            if (settingsChanged)
            {
                if (_editingGame is null)
                {
                    _settings.Games.Remove(game);
                }
                else
                {
                    _settings.ReplaceGame(game, _editingGame);
                }
            }

            if (installationDirectoryMoved
                && previousInstallationDirectory is not null
                && Directory.Exists(installationDirectory)
                && !Directory.Exists(previousInstallationDirectory))
            {
                Directory.Move(installationDirectory, previousInstallationDirectory);
            }

            foreach (string copiedFile in copiedFiles)
            {
                TryDeleteFile(copiedFile);
            }

            if (assetDirectoryCreated)
            {
                TryDeleteEmptyDirectory(assetDirectory, recursive: true);
            }

            if (installationDirectoryCreated)
            {
                TryDeleteEmptyDirectory(installationDirectory, recursive: false);
            }

            throw;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private string CreateUniqueGameId(string name)
    {
        var builder = new StringBuilder();
        foreach (char character in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[builder.Length - 1] != '-')
            {
                builder.Append('-');
            }
        }

        string baseId = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(baseId))
        {
            baseId = "game";
        }

        string candidate = baseId;
        int suffix = 2;
        while (_settings.Games.Any(game => game.Id.Equals(
                   candidate,
                   StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseId}-{suffix++}";
        }

        return candidate;
    }

    private static string NormalizeGameDirectory(string value)
    {
        string normalized = value.Trim().Replace('\\', '/').Trim('/');
        return normalized.Contains('/') ? normalized : $"Games/{normalized}";
    }

    private static string CreateAssetRelativePath(
        string gameId,
        string assetName,
        string sourcePath)
    {
        string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        return $"images/games/{gameId}/{assetName}{extension}";
    }

    private string ResolveInsideRoot(string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(_rootPath)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Выбранный путь выходит за пределы папки лаунчера.");
        }

        return fullPath;
    }

    private static void CopyImage(
        string sourcePath,
        string destinationPath,
        ICollection<string> copiedFiles)
    {
        if (Path.GetFullPath(sourcePath).Equals(
                Path.GetFullPath(destinationPath),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: false);
        copiedFiles.Add(destinationPath);
    }

    private static string RequireValue(string value, string errorMessage)
    {
        string result = value.Trim();
        return string.IsNullOrWhiteSpace(result)
            ? throw new InvalidOperationException(errorMessage)
            : result;
    }

    private static string? SelectImageFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите изображение",
            Filter = "Изображения (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static IReadOnlyList<string>? SelectImageFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите изображения для слайд-шоу",
            Filter = "Изображения (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = true
        };

        return dialog.ShowDialog() == true && dialog.FileNames.Length > 0
            ? dialog.FileNames
            : null;
    }

    private static BitmapImage LoadPreview(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapImage? TryLoadPreview(string path)
    {
        try
        {
            return File.Exists(path) ? LoadPreview(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path, bool recursive)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
