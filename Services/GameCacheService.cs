using GitHubLauncher.Core.Models;
using GithubLauncher.Models;
using GithubLauncher.Services.Logging;
using System.Security.Cryptography;
using System.Text;

namespace GithubLauncher.Services
{
    /// <summary>
    /// Handles file I/O operations for game data: icon caching, version file
    /// persistence, executable preferences, and install path resolution.
    /// </summary>
    public class GameCacheService
    {
        private const string DefaultInstalledVersion = "v0.0.0";

        /// <summary>
        /// Reads the installed version from disk. Creates the version file with
        /// a default value if it does not exist.
        /// </summary>
        public async Task<string?> ReadInstalledVersionAsync(GameInfo game, string gamesFolder)
        {
            if (string.IsNullOrEmpty(game.FolderName))
                return null;

            var gamePath = game.GetInstallPath(gamesFolder);
            var versionFile = Path.Combine(gamePath, "version.txt");

            if (!File.Exists(versionFile))
                return await EnsureInstalledVersionFileAsync(versionFile);

            try
            {
                var version = (await File.ReadAllTextAsync(versionFile).ConfigureAwait(false))?.Trim();
                if (string.IsNullOrWhiteSpace(version))
                    return await EnsureInstalledVersionFileAsync(versionFile);
                return version;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Checks whether the game directory exists on disk.</summary>
        public bool GameDirectoryExists(GameInfo game, string gamesFolder)
        {
            if (string.IsNullOrEmpty(game.FolderName))
                return false;
            var gamePath = game.GetInstallPath(gamesFolder);
            return Directory.Exists(gamePath);
        }

        private static async Task<string> EnsureInstalledVersionFileAsync(string versionFile)
        {
            try
            {
                var versionDirectory = Path.GetDirectoryName(versionFile);
                if (!string.IsNullOrEmpty(versionDirectory))
                    Directory.CreateDirectory(versionDirectory);

                await File.WriteAllTextAsync(versionFile, DefaultInstalledVersion).ConfigureAwait(false);
                return DefaultInstalledVersion;
            }
            catch
            {
                return DefaultInstalledVersion;
            }
        }

        // ---- Icon caching ----

        /// <summary>
        /// Loads a custom icon from the custom-icons cache directory.
        /// Sets <see cref="GameInfo.CustomIconPath"/> if one is found.
        /// </summary>
        public void LoadCustomIcon(GameInfo game, string cacheDirectory)
        {
            if (string.IsNullOrEmpty(game.FolderName))
                return;

            var customIconsDir = Path.Combine(cacheDirectory, "CustomIcons");
            if (!Directory.Exists(customIconsDir))
                return;

            var possibleExtensions = new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico" };
            foreach (var ext in possibleExtensions)
            {
                var fileName = $"{game.FolderName}_custom{ext}";
                var iconPath = Path.Combine(customIconsDir, fileName);
                if (File.Exists(iconPath))
                {
                    try
                    {
                        var attributes = File.GetAttributes(iconPath);
                        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                            File.SetAttributes(iconPath, attributes & ~FileAttributes.ReadOnly);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"Failed to check/modify file attributes for {iconPath}: {ex.Message}");
                    }

                    game.CustomIconPath = iconPath;
                    break;
                }
            }
        }

        /// <summary>
        /// Sets a custom icon for a game by copying the source image into the
        /// cache directory.
        /// </summary>
        public void SetCustomIcon(GameInfo game, string sourcePath, string cacheDirectory)
        {
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                throw new ArgumentException("Source file does not exist or path is invalid.");

            if (string.IsNullOrEmpty(game.FolderName))
                throw new InvalidOperationException("FolderName is required for custom icon operations.");

            var customIconsDir = Path.Combine(cacheDirectory, "CustomIcons");
            Directory.CreateDirectory(customIconsDir);

            var extension = Path.GetExtension(sourcePath);
            var fileName = $"{game.FolderName}_custom{extension}";
            var destinationPath = Path.Combine(customIconsDir, fileName);

            try
            {
                if (!string.IsNullOrEmpty(game.CustomIconPath) && File.Exists(game.CustomIconPath))
                {
                    ClearImageFromMemory(game);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    TryDeleteFileWithRetry(game.CustomIconPath, maxRetries: 3, delayMs: 100);
                }

                using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var destStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    sourceStream.CopyTo(destStream);
                }

                if (File.Exists(destinationPath))
                {
                    var attributes = File.GetAttributes(destinationPath);
                    if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                        File.SetAttributes(destinationPath, attributes & ~FileAttributes.ReadOnly);
                }

                game.CustomIconPath = destinationPath;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to set custom icon: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Removes the custom icon for a game and deletes the cached file.
        /// </summary>
        public void RemoveCustomIcon(GameInfo game)
        {
            if (string.IsNullOrEmpty(game.CustomIconPath))
                return;

            var pathToDelete = game.CustomIconPath;

            try
            {
                game.CustomIconPath = "";
                ClearImageFromMemory(game);

                Task.Run(async () =>
                {
                    await Task.Delay(100);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    try
                    {
                        if (File.Exists(pathToDelete))
                            TryDeleteFileWithRetry(pathToDelete, maxRetries: 5, delayMs: 200);
                    }
                    catch (Exception deleteEx)
                    {
                        Log.Debug($"Warning: Failed to delete custom icon file {pathToDelete}: {deleteEx.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to remove custom icon: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Downloads and caches the default game icon from its URL.
        /// Sets <see cref="GameInfo.CachedDefaultIconPath"/> on success.
        /// </summary>
        public async Task LoadAndCacheDefaultIconAsync(GameInfo game, string cacheDirectory)
        {
            if (string.IsNullOrEmpty(game.FolderName))
                return;

            try
            {
                var iconsDir = Path.Combine(cacheDirectory, "Icons");
                Directory.CreateDirectory(iconsDir);

                var defaultUrl = game.DefaultIconUrl;

                if (!defaultUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !defaultUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var urlHash = GetUrlHash(defaultUrl);
                var extension = Path.GetExtension(defaultUrl);

                if (string.IsNullOrEmpty(extension) || extension.Length > 5 || extension.Contains('?'))
                    extension = ".png";

                var cachedIconPath = Path.Combine(iconsDir, $"{game.FolderName}_{urlHash}{extension}");

                if (File.Exists(cachedIconPath))
                {
                    try
                    {
                        var fileInfo = new FileInfo(cachedIconPath);
                        if (fileInfo.Length > 0)
                        {
                            game.CachedDefaultIconPath = cachedIconPath;
                            Log.Debug($"Using cached icon for {game.Name}: {cachedIconPath}");
                            return;
                        }
                    }
                    catch
                    {
                        try { File.Delete(cachedIconPath); } catch { }
                    }
                }

                Log.Debug($"Downloading icon for {game.Name} from {defaultUrl}");

                using var httpClient = HttpClientFactory.GetDownloadClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Github-Launcher/1.0");

                var iconData = await httpClient.GetByteArrayAsync(defaultUrl);

                await File.WriteAllBytesAsync(cachedIconPath, iconData);
                game.CachedDefaultIconPath = cachedIconPath;
                Log.Debug($"Icon cached for {game.Name}: {cachedIconPath}");
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to cache icon for {game.Name}: {ex.Message}");
            }
        }

        // ---- Executable preferences ----

        /// <summary>
        /// Saves the selected executable path to a file in the game directory.
        /// </summary>
        public void SaveSelectedExecutable(GameInfo game, string executablePath, string gamesFolder)
        {
            if (string.IsNullOrEmpty(game.FolderName) || string.IsNullOrEmpty(executablePath))
                return;

            try
            {
                var gamePath = game.GetInstallPath(gamesFolder);
                var selectedExePath = Path.Combine(gamePath, "selected_executable.txt");
                File.WriteAllText(selectedExePath, executablePath);
                Log.Debug($"Saved selected executable for {game.Name}: {executablePath}");
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to save selected executable for {game.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads the previously selected executable path from the game directory.
        /// Returns null if the saved path no longer exists.
        /// </summary>
        public string? LoadSelectedExecutable(GameInfo game, string gamesFolder)
        {
            if (string.IsNullOrEmpty(game.FolderName))
                return null;

            try
            {
                var gamePath = game.GetInstallPath(gamesFolder);
                var selectedExePath = Path.Combine(gamePath, "selected_executable.txt");

                if (File.Exists(selectedExePath))
                {
                    var savedPath = File.ReadAllText(selectedExePath).Trim();
                    if (File.Exists(savedPath))
                    {
                        Log.Debug($"Loaded selected executable for {game.Name}: {savedPath}");
                        return savedPath;
                    }
                    else
                    {
                        File.Delete(selectedExePath);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to load selected executable for {game.Name}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Clears the saved executable preference.
        /// </summary>
        public void ClearSelectedExecutable(GameInfo game, string gamesFolder)
        {
            if (string.IsNullOrEmpty(game.FolderName))
                return;

            try
            {
                var gamePath = game.GetInstallPath(gamesFolder);
                var selectedExePath = Path.Combine(gamePath, "selected_executable.txt");

                if (File.Exists(selectedExePath))
                    File.Delete(selectedExePath);

                game.SelectedExecutable = null;
                Log.Debug($"Cleared selected executable for {game.Name}");
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to clear selected executable for {game.Name}: {ex.Message}");
            }
        }

        /// <summary>Deletes the version.txt file for a game.</summary>
        public void DeleteVersionFile(GameInfo game, string gamesFolder)
        {
            if (string.IsNullOrEmpty(game.FolderName))
                return;

            try
            {
                var gamePath = game.GetInstallPath(gamesFolder);
                var versionFile = Path.Combine(gamePath, "version.txt");
                if (File.Exists(versionFile))
                    File.Delete(versionFile);
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to delete version file for {game.Name}: {ex.Message}");
            }
        }

        /// <summary>Writes a LastPlayed.txt timestamp.</summary>
        public void UpdateLastPlayedTime(GameInfo game, string gamesFolder)
        {
            if (string.IsNullOrEmpty(game.FolderName))
                return;

            try
            {
                var gamePath = game.GetInstallPath(gamesFolder);
                if (!Directory.Exists(gamePath))
                {
                    Log.Debug($"Cannot update LastPlayed: directory does not exist: {gamePath}");
                    return;
                }

                var lastPlayedPath = Path.Combine(gamePath, "LastPlayed.txt");
                var currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.WriteAllText(lastPlayedPath, currentTime);
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to update LastPlayed.txt for {game.Name}: {ex.Message}");
            }
        }

        // ---- Private helpers ----

        private static string GetUrlHash(string url)
        {
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(url));
            return Convert.ToHexString(hashBytes).Substring(0, 16).ToLowerInvariant();
        }

        private static void ClearImageFromMemory(GameInfo game)
        {
            // Force the UI to re-read the icon path
            game.CustomIconPath = game.CustomIconPath;
        }

        internal static void TryDeleteFileWithRetry(string filePath, int maxRetries = 5, int delayMs = 200)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        var attributes = File.GetAttributes(filePath);
                        if ((attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
                            File.SetAttributes(filePath, FileAttributes.Normal);

                        GC.Collect();
                        GC.WaitForPendingFinalizers();

                        File.Delete(filePath);
                        Log.Debug($"Successfully deleted file: {filePath}");
                        return;
                    }
                    else
                    {
                        return;
                    }
                }
                catch (IOException ex) when (i < maxRetries - 1)
                {
                    Log.Debug($"Attempt {i + 1}/{maxRetries} failed to delete {filePath}: {ex.Message}");
                    System.Threading.Thread.Sleep(delayMs * (i + 1));
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                catch (UnauthorizedAccessException ex) when (i < maxRetries - 1)
                {
                    Log.Debug($"Attempt {i + 1}/{maxRetries} - Access denied for {filePath}: {ex.Message}");
                    try { File.SetAttributes(filePath, FileAttributes.Normal); } catch { }
                    System.Threading.Thread.Sleep(delayMs * (i + 1));
                }
            }

            Log.Debug($"Unable to delete file after {maxRetries} attempts: {filePath}. File may be in use.");
        }
    }
}
