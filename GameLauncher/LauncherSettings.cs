using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameLauncher;

internal sealed class LauncherSettings
{
    public string LauncherName { get; init; } = "Game Launcher";
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

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(LauncherName))
        {
            throw new InvalidOperationException("В настройках не задано название лаунчера.");
        }

        if (Games.Count == 0)
        {
            throw new InvalidOperationException("В настройках не добавлено ни одной игры.");
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
    public string GameDirectory { get; init; } = string.Empty;
    public string GameExecutable { get; init; } = string.Empty;
    public string VersionFile { get; init; } = string.Empty;
    public string? CoverImage { get; init; }
    public string? BackgroundImage { get; init; }
    public string VersionUrl { get; init; } = string.Empty;
    public string PackageUrl { get; init; } = string.Empty;

    [JsonIgnore]
    public Uri VersionUri { get; private set; } = null!;

    [JsonIgnore]
    public Uri PackageUri { get; private set; } = null!;

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

        ValidateRelativePath(GameDirectory, "папка игры");
        ValidateRelativePath(GameExecutable, "исполняемый файл игры");

        if (string.IsNullOrWhiteSpace(VersionFile)
            || Path.IsPathRooted(VersionFile)
            || Path.GetFileName(VersionFile) != VersionFile)
        {
            throw new InvalidOperationException(
                $"У игры '{Name}' файл версии должен быть простым именем файла.");
        }

        ValidateOptionalRelativePath(CoverImage, "обложка");
        ValidateOptionalRelativePath(BackgroundImage, "фон");

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
