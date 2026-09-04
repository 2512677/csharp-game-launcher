using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace GameLauncher;

internal enum LauncherPhase
{
    Checking,
    Downloading,
    Installing
}

internal sealed record LauncherProgress(
    LauncherPhase Phase,
    string Message,
    int? Percentage = null);

internal sealed record GameCheckResult(
    Version? LocalVersion,
    Version OnlineVersion,
    bool IsInstalled)
{
    public bool UpdateAvailable => !IsInstalled
        || LocalVersion is null
        || OnlineVersion.CompareTo(LocalVersion) > 0;
}

internal sealed record UpdateResult(Version Version, string Message);

internal sealed class GameUpdater
{
    private const int BufferSize = 81920;

    private readonly HttpClient _httpClient;
    private readonly GameSettings _settings;
    private readonly string _rootPath;
    private readonly string _versionFilePath;
    private readonly string _gameDirectoryPath;

    public GameUpdater(HttpClient httpClient, GameSettings settings, string rootPath)
    {
        _httpClient = httpClient;
        _settings = settings;
        _rootPath = Path.GetFullPath(rootPath);
        _versionFilePath = ResolveInsideRoot(settings.VersionFile);
        _gameDirectoryPath = ResolveInsideRoot(settings.GameDirectory);
        GameExecutablePath = ResolveInsideGameDirectory(settings.GameExecutable);
    }

    public string GameExecutablePath { get; }

    public bool IsGameInstalled => File.Exists(GameExecutablePath);

    public Version? LocalVersion
    {
        get
        {
            if (!File.Exists(_versionFilePath))
            {
                return null;
            }

            string value = File.ReadAllText(_versionFilePath).Trim();
            return Version.TryParse(value, out Version? version) ? version : null;
        }
    }

    public async Task<GameCheckResult> CheckAsync(
        IProgress<LauncherProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new LauncherProgress(
            LauncherPhase.Checking,
            "Проверяем наличие обновлений…"));

        Version onlineVersion = await DownloadVersionAsync(cancellationToken);
        return new GameCheckResult(LocalVersion, onlineVersion, IsGameInstalled);
    }

    public async Task<UpdateResult> InstallOrUpdateAsync(
        Version onlineVersion,
        IProgress<LauncherProgress> progress,
        CancellationToken cancellationToken)
    {
        string downloadMessage = IsGameInstalled
            ? $"Загружаем обновление {onlineVersion}…"
            : $"Загружаем игру {onlineVersion}…";
        progress.Report(new LauncherProgress(LauncherPhase.Downloading, downloadMessage, 0));

        string archivePath = Path.Combine(
            _rootPath,
            $".launcher-download-{Guid.NewGuid():N}.zip");

        try
        {
            await DownloadPackageAsync(archivePath, progress, cancellationToken);
            progress.Report(new LauncherProgress(
                LauncherPhase.Installing,
                "Устанавливаем файлы…"));
            InstallPackage(archivePath, onlineVersion);
        }
        finally
        {
            TryDeleteFile(archivePath);
        }

        return new UpdateResult(onlineVersion, "Обновление установлено. Можно играть.");
    }

    private async Task<Version> DownloadVersionAsync(CancellationToken cancellationToken)
    {
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        requestCancellation.CancelAfter(TimeSpan.FromSeconds(30));

        using HttpResponseMessage response = await _httpClient.GetAsync(
            _settings.VersionUri,
            HttpCompletionOption.ResponseContentRead,
            requestCancellation.Token);
        response.EnsureSuccessStatusCode();

        string value = (await response.Content.ReadAsStringAsync(requestCancellation.Token)).Trim();
        if (!Version.TryParse(value, out Version? version))
        {
            throw new InvalidDataException(
                "Сервер версии вернул неверный ответ. Ожидается номер вида 1.2.3.");
        }

        return version;
    }

    private async Task DownloadPackageAsync(
        string destinationPath,
        IProgress<LauncherProgress> progress,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(
            _settings.PackageUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: true);

        byte[] buffer = new byte[BufferSize];
        long downloadedBytes = 0;
        int lastPercentage = -1;

        while (true)
        {
            int bytesRead = await input.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            downloadedBytes += bytesRead;

            if (totalBytes is > 0)
            {
                int percentage = (int)Math.Clamp(downloadedBytes * 100 / totalBytes.Value, 0, 100);
                if (percentage != lastPercentage)
                {
                    lastPercentage = percentage;
                    progress.Report(new LauncherProgress(
                        LauncherPhase.Downloading,
                        $"Загружаем файлы… {percentage}%",
                        percentage));
                }
            }
        }

        await output.FlushAsync(cancellationToken);

        if (downloadedBytes == 0)
        {
            throw new InvalidDataException("Сервер вернул пустой архив игры.");
        }
    }

    private void InstallPackage(string archivePath, Version onlineVersion)
    {
        string operationId = Guid.NewGuid().ToString("N");
        string stagingPath = Path.Combine(_rootPath, $".launcher-staging-{operationId}");
        string backupPath = Path.Combine(_rootPath, $".launcher-backup-{operationId}");
        string versionTempPath = _versionFilePath + ".tmp";
        bool existingGameMoved = false;
        bool newGameMoved = false;

        try
        {
            Directory.CreateDirectory(stagingPath);
            try
            {
                ZipFile.ExtractToDirectory(archivePath, stagingPath, overwriteFiles: true);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    "Загруженный файл не является корректным ZIP-архивом.",
                    exception);
            }

            string packagedGamePath = FindPackagedGameDirectory(stagingPath);
            Directory.CreateDirectory(Path.GetDirectoryName(_gameDirectoryPath)!);

            if (Directory.Exists(_gameDirectoryPath))
            {
                Directory.Move(_gameDirectoryPath, backupPath);
                existingGameMoved = true;
            }

            try
            {
                Directory.Move(packagedGamePath, _gameDirectoryPath);
                newGameMoved = true;

                File.WriteAllText(versionTempPath, onlineVersion.ToString());
                File.Move(versionTempPath, _versionFilePath, overwrite: true);
            }
            catch
            {
                if (newGameMoved && Directory.Exists(_gameDirectoryPath))
                {
                    Directory.Move(_gameDirectoryPath, packagedGamePath);
                }

                if (existingGameMoved && Directory.Exists(backupPath))
                {
                    Directory.Move(backupPath, _gameDirectoryPath);
                }

                throw;
            }

            if (existingGameMoved)
            {
                TryDeleteDirectory(backupPath);
            }
        }
        finally
        {
            TryDeleteFile(versionTempPath);
            TryDeleteDirectory(stagingPath);
        }
    }

    private string FindPackagedGameDirectory(string stagingPath)
    {
        string configuredDirectory = Path.Combine(stagingPath, _settings.GameDirectory);
        string configuredExecutable = Path.Combine(configuredDirectory, _settings.GameExecutable);
        if (File.Exists(configuredExecutable))
        {
            return configuredDirectory;
        }

        string rootExecutable = Path.Combine(stagingPath, _settings.GameExecutable);
        if (File.Exists(rootExecutable))
        {
            return stagingPath;
        }

        string executableFileName = Path.GetFileName(_settings.GameExecutable);
        string normalizedExecutable = _settings.GameExecutable.Replace(
            Path.AltDirectorySeparatorChar,
            Path.DirectorySeparatorChar);

        foreach (string candidate in Directory.EnumerateFiles(
                     stagingPath,
                     executableFileName,
                     SearchOption.AllDirectories))
        {
            string relativeCandidate = Path.GetRelativePath(stagingPath, candidate);
            if (!relativeCandidate.EndsWith(
                    normalizedExecutable,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string packageRoot = relativeCandidate[..^normalizedExecutable.Length]
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(packageRoot)
                ? stagingPath
                : Path.Combine(stagingPath, packageRoot);
        }

        throw new InvalidDataException(
            $"В архиве не найден файл игры '{_settings.GameExecutable}'.");
    }

    private string ResolveInsideRoot(string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(_rootPath)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Путь в настройках выходит за пределы папки лаунчера.");
        }

        return fullPath;
    }

    private string ResolveInsideGameDirectory(string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(_gameDirectoryPath, relativePath));
        string gameDirectoryWithSeparator = Path.TrimEndingDirectorySeparator(_gameDirectoryPath)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(gameDirectoryWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Путь к исполняемому файлу выходит за пределы папки игры.");
        }

        return fullPath;
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
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
