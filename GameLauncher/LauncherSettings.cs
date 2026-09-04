using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLauncher;

internal sealed class LauncherSettings
{
    public string LauncherName { get; init; } = "Sherdor Games Launcher";
    public List<GameSettings> Games { get; init; } = [];

    public static LauncherSettings Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new InvalidOperationException(
                $"Не найден файл настроек '{Path.GetFileName(filePath)}'.");
        }

        string json = File.ReadAllText(filePath);
        LauncherSettings settings = JsonSerializer.Deserialize<LauncherSettings>(
            json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            }) ?? throw new InvalidOperationException("Файл настроек пуст или повреждён.");

        settings.Validate();
        return settings;
    }

    public void AddGame(GameSettings game)
    {
        Games.Add(game);
        try
        {
            Validate();
        }
        catch
        {
            Games.Remove(game);
            throw;
        }
    }

    public void ReplaceGame(GameSettings existingGame, GameSettings updatedGame)
    {
        int index = Games.IndexOf(existingGame);
        if (index < 0)
        {
            throw new InvalidOperationException("Редактируемая игра не найдена в настройках.");
        }

        Games[index] = updatedGame;
        try
        {
            Validate();
        }
        catch
        {
            Games[index] = existingGame;
            throw;
        }
    }

    public void Save(string filePath)
    {
        Validate();

        string json = JsonSerializer.Serialize(
            this,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        string temporaryPath = filePath + ".tmp";

        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(LauncherName))
        {
            throw new InvalidOperationException("В настройках не задано название лаунчера.");
        }

        var gameIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gameDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var versionFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GameSettings game in Games)
        {
            game.Validate();
            if (!gameIds.Add(game.Id))
            {
                throw new InvalidOperationException(
                    $"Идентификатор игры '{game.Id}' используется несколько раз.");
            }

            if (!gameDirectories.Add(game.GameDirectory))
            {
                throw new InvalidOperationException(
                    $"Папка игры '{game.GameDirectory}' используется несколько раз.");
            }

            if (!versionFiles.Add(game.VersionFile))
            {
                throw new InvalidOperationException(
                    $"Файл версии '{game.VersionFile}' используется несколько раз.");
            }
        }
    }
}

internal sealed class GameSettings
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = GameCatalog.ReleaseCategory;
    public List<string> Genres { get; init; } = [];
    public string GameDirectory { get; init; } = string.Empty;
    public string GameExecutable { get; init; } = string.Empty;
    public string VersionFile { get; init; } = string.Empty;
    public string? LogoImage { get; init; }
    public string? CoverImage { get; init; }
    public string? BackgroundImage { get; init; }
    public List<string> SlideshowImages { get; init; } = [];
    public string? YoutubeUrl { get; init; }
    public string VersionUrl { get; init; } = string.Empty;
    public string PackageUrl { get; init; } = string.Empty;

    [JsonIgnore]
    public Uri VersionUri { get; private set; } = null!;

    [JsonIgnore]
    public Uri PackageUri { get; private set; } = null!;

    [JsonIgnore]
    public string? YoutubeVideoId { get; private set; }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id)
            || Id.Any(character => !char.IsLetterOrDigit(character)
                && character is not '-' and not '_'))
        {
            throw new InvalidOperationException(
                "Идентификатор игры может содержать только буквы, цифры, дефис и подчёркивание.");
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException($"У игры '{Id}' не задано название.");
        }

        if (!GameCatalog.IsKnownCategory(Category))
        {
            throw new InvalidOperationException(
                $"У игры '{Name}' указана неизвестная категория сборки.");
        }

        if (Genres.Any(genre => !GameCatalog.IsKnownGenre(genre)))
        {
            throw new InvalidOperationException(
                $"У игры '{Name}' указан неизвестный жанр.");
        }

        if (Genres.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Genres.Count)
        {
            throw new InvalidOperationException(
                $"У игры '{Name}' один и тот же жанр указан несколько раз.");
        }

        ValidateRelativePath(GameDirectory, "папка игры");
        ValidateRelativePath(GameExecutable, "исполняемый файл игры");

        if (string.IsNullOrWhiteSpace(VersionFile)
            || Path.IsPathRooted(VersionFile)
            || Path.GetFileName(VersionFile) != VersionFile)
        {
            throw new InvalidOperationException(
                $"У игры '{Name}' файл версии должен быть простым именем файла.");
        }

        ValidateOptionalRelativePath(LogoImage, "логотип");
        ValidateOptionalRelativePath(CoverImage, "обложка");
        ValidateOptionalRelativePath(BackgroundImage, "фон");
        foreach (string slideshowImage in SlideshowImages)
        {
            ValidateRelativePath(slideshowImage, "изображение слайд-шоу");
        }

        YoutubeVideoId = ParseYoutubeVideoId(YoutubeUrl);
        VersionUri = NormalizeDownloadUri(ParseHttpUri(VersionUrl, "адрес версии"));
        PackageUri = NormalizeDownloadUri(ParseHttpUri(PackageUrl, "адрес архива игры"));
    }

    private void ValidateRelativePath(string value, string settingName)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            throw new InvalidOperationException(
                $"У игры '{Name}' параметр '{settingName}' должен быть относительным путём.");
        }

        string[] segments = value.Replace('\\', '/').Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new InvalidOperationException(
                $"У игры '{Name}' параметр '{settingName}' содержит недопустимый путь.");
        }
    }

    private void ValidateOptionalRelativePath(string? value, string settingName)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            ValidateRelativePath(value, settingName);
        }
    }

    private Uri ParseHttpUri(string value, string settingName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException(
                $"У игры '{Name}' указан некорректный {settingName}.");
        }

        return uri;
    }

    private string? ParseYoutubeVideoId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Uri uri = ParseHttpUri(value.Trim(), "адрес YouTube-видео");
        string host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        string[] segments = uri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        string? videoId = host switch
        {
            "youtu.be" when segments.Length >= 1 => segments[0],
            "youtube.com" or "m.youtube.com" when uri.AbsolutePath.Equals(
                "/watch",
                StringComparison.OrdinalIgnoreCase) => GetQueryValue(uri, "v"),
            "youtube.com" or "m.youtube.com" or "youtube-nocookie.com"
                when segments.Length >= 2
                    && (segments[0].Equals("embed", StringComparison.OrdinalIgnoreCase)
                        || segments[0].Equals("shorts", StringComparison.OrdinalIgnoreCase))
                => segments[1],
            _ => null
        };

        if (string.IsNullOrWhiteSpace(videoId)
            || videoId.Length > 64
            || videoId.Any(character => !char.IsLetterOrDigit(character)
                && character is not '-' and not '_'))
        {
            throw new InvalidOperationException(
                $"У игры '{Name}' указана некорректная ссылка на YouTube-видео.");
        }

        return videoId;
    }

    private static Uri NormalizeDownloadUri(Uri uri)
    {
        if (!uri.Host.Equals("drive.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        string? fileId = TryGetGoogleDriveFileId(uri);
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return uri;
        }

        string query = $"id={Uri.EscapeDataString(fileId)}&export=download&confirm=t";
        string? resourceKey = GetQueryValue(uri, "resourcekey");
        if (!string.IsNullOrWhiteSpace(resourceKey))
        {
            query += $"&resourcekey={Uri.EscapeDataString(resourceKey)}";
        }

        return new UriBuilder(Uri.UriSchemeHttps, "drive.usercontent.google.com")
        {
            Path = "/download",
            Query = query
        }.Uri;
    }

    private static string? TryGetGoogleDriveFileId(Uri uri)
    {
        string[] segments = uri.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length >= 3
            && segments[0].Equals("file", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("d", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.UnescapeDataString(segments[2]);
        }

        return GetQueryValue(uri, "id");
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        foreach (string pair in uri.Query.TrimStart('?').Split(
                     '&',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length > 0
                && Uri.UnescapeDataString(parts[0]).Equals(
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            }
        }

        return null;
    }
}

internal sealed record BuildCategoryOption(string Id, string DisplayName);

internal static class GameCatalog
{
    public const string EarlyAccessCategory = "early-access";
    public const string DemoCategory = "demo";
    public const string ReleaseCategory = "release";

    public static IReadOnlyList<BuildCategoryOption> BuildCategories { get; } =
    [
        new(EarlyAccessCategory, "Ранний доступ"),
        new(DemoCategory, "Демо"),
        new(ReleaseCategory, "Релиз")
    ];

    public static IReadOnlyList<string> Genres { get; } =
    [
        "Экшен",
        "Приключения",
        "RPG",
        "Стратегия",
        "Симулятор",
        "Гонки",
        "Спорт",
        "Шутер",
        "Файтинг",
        "Платформер",
        "Головоломка",
        "Хоррор",
        "Выживание",
        "Песочница",
        "Казуальная",
        "Онлайн"
    ];

    public static bool IsKnownCategory(string? category)
    {
        return BuildCategories.Any(option => option.Id.Equals(
            category,
            StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsKnownGenre(string? genre)
    {
        return Genres.Contains(genre ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    public static string GetCategoryDisplayName(string? category)
    {
        return BuildCategories.FirstOrDefault(option => option.Id.Equals(
                   category,
                   StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? "Релиз";
    }
}
