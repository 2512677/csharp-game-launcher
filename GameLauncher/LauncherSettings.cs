using System.IO;
using System.Text.Json;

namespace GameLauncher;

internal sealed class LauncherSettings
{
    public string GameName { get; init; } = string.Empty;
    public string GameDirectory { get; init; } = string.Empty;
    public string GameExecutable { get; init; } = string.Empty;
    public string VersionFile { get; init; } = "Version.txt";
    public string? BackgroundImage { get; init; }
    public string VersionUrl { get; init; } = string.Empty;
    public string PackageUrl { get; init; } = string.Empty;

    public Uri VersionUri { get; private set; } = null!;
    public Uri PackageUri { get; private set; } = null!;

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
        if (string.IsNullOrWhiteSpace(GameName))
        {
            throw new InvalidOperationException("В настройках не задано название игры.");
        }

        if (string.IsNullOrWhiteSpace(GameDirectory)
            || Path.IsPathRooted(GameDirectory)
            || GameDirectory is "." or ".."
            || GameDirectory.Contains(Path.DirectorySeparatorChar)
            || GameDirectory.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException("Папка игры должна быть простым относительным именем.");
        }

        if (string.IsNullOrWhiteSpace(GameExecutable) || Path.IsPathRooted(GameExecutable))
        {
            throw new InvalidOperationException("Исполняемый файл игры должен быть относительным путём.");
        }

        if (string.IsNullOrWhiteSpace(VersionFile)
            || Path.IsPathRooted(VersionFile)
            || Path.GetFileName(VersionFile) != VersionFile)
        {
            throw new InvalidOperationException("Файл версии должен быть простым именем файла.");
        }

        if (!string.IsNullOrWhiteSpace(BackgroundImage) && Path.IsPathRooted(BackgroundImage))
        {
            throw new InvalidOperationException("Фоновое изображение должно быть относительным путём.");
        }

        VersionUri = NormalizeDownloadUri(ParseHttpUri(VersionUrl, "адрес версии"));
        PackageUri = NormalizeDownloadUri(ParseHttpUri(PackageUrl, "адрес архива игры"));
    }

    private static Uri ParseHttpUri(string value, string settingName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"Некорректный {settingName} в настройках.");
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
