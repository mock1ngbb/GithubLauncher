using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GitHubLauncher.Core.Models;
using GitHubLauncher.Core.Services;
using GithubLauncher.Models;
using GithubLauncher.Services.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GithubLauncher.Services
{
    /// <summary>
    /// Handles all GitHub API interaction, release fetching, download/install
    /// pipelines, and game launching. Receives a <see cref="GameCacheService"/>
    /// for file I/O operations that are needed during these workflows.
    /// </summary>
    public class GameApiService
    {
        private readonly GameCacheService _cacheService;

        public GameApiService(GameCacheService cacheService)
        {
            _cacheService = cacheService;
        }

        // ================================================================
        //  Status checking
        // ================================================================

        /// <summary>
        /// Checks the install status of a game and optionally fetches the latest
        /// version from GitHub. Updates <see cref="GameInfo.Status"/>,
        /// <see cref="GameInfo.InstalledVersion"/>, and
        /// <see cref="GameInfo.LatestVersion"/>.
        /// </summary>
        public async Task CheckStatusAsync(GameInfo game, HttpClient httpClient, string gamesFolder, bool forceUpdateCheck = false)
        {
            if (string.IsNullOrEmpty(game.FolderName))
            {
                Log.Warn($"FolderName is null or empty for game {game.Name}");
                game.Status = GameStatus.NotInstalled;
                return;
            }

            game.IsLoading = true;

            try
            {
                var gamePath = game.GetInstallPath(gamesFolder);
                bool directoryExists = _cacheService.GameDirectoryExists(game, gamesFolder);

                bool isInstalled = false;
                if (directoryExists)
                {
                    var version = await _cacheService.ReadInstalledVersionAsync(game, gamesFolder).ConfigureAwait(false);
                    game.InstalledVersion = version;
                    game.Status = GameStatus.Installed;
                    isInstalled = true;
                }
                else
                {
                    game.Status = GameStatus.NotInstalled;
                    game.InstalledVersion = "";
                }

                if (forceUpdateCheck)
                {
                    await CheckLatestVersionAsync(game, httpClient, forceCheck: true).ConfigureAwait(false);
                }
                else if (isInstalled)
                {
                    if (GitHubApiCache.NeedsUpdateCheck(game.Repository ?? string.Empty, isInstalledGame: true))
                    {
                        await CheckLatestVersionAsync(game, httpClient).ConfigureAwait(false);
                    }
                    else if (GitHubApiCache.TryGetCachedVersion(game.Repository ?? string.Empty, out var cache) && cache != null)
                    {
                        game.LatestVersion = cache.Version;
                        game.CachedRelease = cache.CachedRelease;
                    }
                }
                else
                {
                    if (GitHubApiCache.NeedsUpdateCheck(game.Repository ?? string.Empty, isInstalledGame: false))
                    {
                        await CheckLatestVersionAsync(game, httpClient).ConfigureAwait(false);
                    }
                    else if (GitHubApiCache.TryGetCachedVersion(game.Repository ?? string.Empty, out var cache) && cache != null)
                    {
                        game.LatestVersion = cache.Version;
                        game.CachedRelease = cache.CachedRelease;
                    }
                }

                if (isInstalled && string.IsNullOrWhiteSpace(game.InstalledVersion))
                {
                    game.InstalledVersion = string.IsNullOrWhiteSpace(game.LatestVersion)
                        ? "Unknown"
                        : "v0.0.0";
                }

                if (isInstalled)
                    game.RefreshInstalledStatus();
            }
            catch (Exception ex)
            {
                Log.Error($"Error checking status for {game.Name}", ex);
                game.Status = GameStatus.NotInstalled;
            }
            finally
            {
                game.IsLoading = false;
            }
        }

        private async Task CheckLatestVersionAsync(GameInfo game, HttpClient httpClient)
        {
            await CheckLatestVersionAsync(game, httpClient, forceCheck: false);
        }

        private async Task CheckLatestVersionAsync(GameInfo game, HttpClient httpClient, bool forceCheck)
        {
            if (string.IsNullOrEmpty(game.Repository))
            {
                Log.Warn($"Repository is null or empty for game {game.Name}");
                return;
            }

            try
            {
                if (!forceCheck && !GitHubApiCache.NeedsUpdateCheck(game.Repository))
                {
                    if (GitHubApiCache.TryGetCachedVersion(game.Repository, out var cachedData) && cachedData != null)
                    {
                        game.LatestVersion = cachedData.Version;
                        game.CachedRelease = cachedData.CachedRelease;
                        game.RefreshInstalledStatus();
                    }
                    return;
                }

                var result = await GitHubReleaseService.FetchReleasesAsync(
                    httpClient,
                    game.Repository,
                    GetGitHubApiToken(),
                    GitHubApiCache.GetETag(game.Repository)).ConfigureAwait(false);

                if (result.IsNotModified)
                {
                    if (GitHubApiCache.TryGetCachedVersion(game.Repository, out var existingCache) && existingCache != null)
                    {
                        game.LatestVersion = existingCache.Version;
                        game.CachedRelease = existingCache.CachedRelease;
                        GitHubApiCache.SetCache(game.Repository, existingCache.Version, existingCache.ETag, existingCache.CachedRelease);
                        game.RefreshInstalledStatus();
                    }
                    return;
                }

                var latestRelease = GameInfo.SelectLatestRelease(result.Releases);
                if (latestRelease != null && !string.IsNullOrWhiteSpace(latestRelease.tag_name))
                {
                    game.LatestVersion = latestRelease.tag_name;
                    game.CachedRelease = latestRelease;
                    GitHubApiCache.SetCache(game.Repository, latestRelease.tag_name, result.ETag ?? string.Empty, latestRelease);
                    game.RefreshInstalledStatus();
                }
                else
                {
                    Log.Debug($"No releases found for {game.Repository}");
                }
            }
            catch (HttpRequestException ex)
            {
                Log.Debug($"Network error fetching latest version for {game.Repository}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log.Debug($"Error fetching latest version for {game.Repository}: {ex.Message}");
            }
        }

        // ================================================================
        //  Force update
        // ================================================================

        public async Task ForceUpdateAsync(GameInfo game, HttpClient httpClient, string gamesFolder)
        {
            if (string.IsNullOrWhiteSpace(game.FolderName))
                throw new InvalidOperationException("App configuration is invalid (missing folder name).");

            if (string.IsNullOrWhiteSpace(game.Repository))
                throw new InvalidOperationException("App configuration is invalid (missing repository).");

            var gamePath = game.GetInstallPath(gamesFolder);
            if (!Directory.Exists(gamePath))
                throw new DirectoryNotFoundException($"App folder not found: {gamePath}");

            game.IsLoading = true;
            try
            {
                game.CachedRelease = null;
                game.LatestVersion = string.Empty;
                GitHubApiCache.RemoveCache(game.Repository);

                _cacheService.DeleteVersionFile(game, gamesFolder);

                await CheckStatusAsync(game, httpClient, gamesFolder, forceUpdateCheck: true).ConfigureAwait(false);
            }
            finally
            {
                game.IsLoading = false;
            }
        }

        // ================================================================
        //  Action orchestrator (download / install / launch)
        // ================================================================

        public async Task PerformActionAsync(GameInfo game, HttpClient httpClient, string gamesFolder, AppSettings settings)
        {
            if (string.IsNullOrEmpty(game.FolderName))
            {
                await ShowMessageBoxAsync("App configuration is invalid (missing folder name).", "Configuration Error");
                return;
            }

            switch (game.Status)
            {
                case GameStatus.NotInstalled:
                case GameStatus.UpdateAvailable:
                    await DownloadAndInstallAsync(game, httpClient, gamesFolder, game.CachedRelease, settings, game.Status);
                    break;

                case GameStatus.Installed:
                    await LaunchAsync(game, gamesFolder);
                    break;
            }
        }

        // ================================================================
        //  Release fetching
        // ================================================================

        public async Task<List<GitHubRelease>> FetchReleasesAsync(GameInfo game, HttpClient httpClient)
        {
            if (string.IsNullOrWhiteSpace(game.Repository))
                return [];

            return await GitHubReleaseService.FetchReleasesWithAssetsAsync(
                httpClient,
                game.Repository,
                GetGitHubApiToken()).ConfigureAwait(false);
        }

        // ================================================================
        //  Install a specific release
        // ================================================================

        public async Task InstallReleaseAsync(GameInfo game, HttpClient httpClient, string gamesFolder, AppSettings settings, GitHubRelease release, GitHubAsset selectedAsset)
        {
            game.SelectedDownload = selectedAsset;
            await DownloadAndInstallAsync(game, httpClient, gamesFolder, release, settings, game.Status);
        }

        // ================================================================
        //  Download and install pipeline
        // ================================================================

        private async Task DownloadAndInstallAsync(GameInfo game, HttpClient httpClient, string gamesFolder, GitHubRelease? latestRelease, AppSettings settings, GameStatus status)
        {
            if (string.IsNullOrEmpty(game.FolderName))
            {
                await ShowMessageBoxAsync("App configuration is invalid (missing folder name).", "Configuration Error");
                return;
            }

            if (string.IsNullOrEmpty(game.Repository))
            {
                await ShowMessageBoxAsync("App configuration is invalid (missing repository).", "Configuration Error");
                return;
            }

            try
            {
                game.Status = (status == GameStatus.UpdateAvailable) ? GameStatus.Updating : GameStatus.Downloading;
                game.DownloadProgress = 0;

                string platformIdentifier = GetPlatformIdentifier(settings);
                var gamePath = game.GetInstallPath(gamesFolder);
                var versionFile = Path.Combine(gamePath, "version.txt");

                if (latestRelease == null)
                {
                    if (GitHubApiCache.TryGetCachedVersion(game.Repository, out var cache) && cache?.CachedRelease != null)
                    {
                        latestRelease = cache.CachedRelease;
                    }
                    else
                    {
                        game.DownloadProgress = 5;
                        var releaseResult = await GitHubReleaseService.FetchReleasesAsync(
                            httpClient,
                            game.Repository,
                            GetGitHubApiToken()).ConfigureAwait(false);

                        if (releaseResult.Releases.Count == 0)
                        {
                            await ShowMessageBoxAsync($"No releases found for {game.Name}.", "No Releases");
                            game.Status = GameStatus.NotInstalled;
                            game.DownloadProgress = 0;
                            return;
                        }

                        latestRelease = GameInfo.SelectLatestRelease(releaseResult.Releases);

                        if (latestRelease == null)
                        {
                            await ShowMessageBoxAsync($"No valid releases found for {game.Name}.", "No Releases");
                            game.Status = GameStatus.NotInstalled;
                            game.DownloadProgress = 0;
                            return;
                        }

                        GitHubApiCache.SetCache(game.Repository, latestRelease.tag_name, releaseResult.ETag ?? string.Empty, latestRelease);
                    }
                }

                game.DownloadProgress = 10;

                if (File.Exists(versionFile))
                {
                    var existingVersion = (await File.ReadAllTextAsync(versionFile).ConfigureAwait(false))?.Trim();
                    if (existingVersion == latestRelease.tag_name)
                    {
                        game.Status = GameStatus.Installed;
                        game.InstalledVersion = existingVersion;
                        game.LatestVersion = latestRelease.tag_name;
                        game.DownloadProgress = 0;
                        return;
                    }
                }

                var availableAssets = GetDownloadableAssets(latestRelease);

                if (availableAssets.Count == 0)
                {
                    await ShowMessageBoxAsync($"No download files found for {game.Name}.", "No Assets");
                    game.Status = GameStatus.NotInstalled;
                    game.DownloadProgress = 0;
                    return;
                }

                game.AvailableDownloads = availableAssets;

                GitHubAsset? asset = null;

                if (availableAssets.Count > 1 && game.SelectedDownload == null)
                {
                    game.Status = GameStatus.NotInstalled;
                    game.DownloadProgress = 0;
                    return;
                }

                asset = game.SelectedDownload ?? availableAssets[0];

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    bool isWindowsFile = PlatformAssetMatcher.IsWindowsAsset(asset.name);

                    if (isWindowsFile)
                    {
                        if (!IsWindowsRunnerAvailable(settings))
                        {
                            bool shouldContinueAnyway = await ShowWineNotFoundWarning();
                            if (!shouldContinueAnyway)
                            {
                                game.Status = GameStatus.NotInstalled;
                                game.DownloadProgress = 0;
                                return;
                            }
                        }
                        else
                        {
                            bool shouldContinue = await ShowWineDownloadWarning();
                            if (!shouldContinue)
                            {
                                game.Status = GameStatus.NotInstalled;
                                game.DownloadProgress = 0;
                                return;
                            }
                        }
                    }
                }

                var downloadPath = Path.Combine(Path.GetTempPath(), asset.name);

                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, asset.browser_download_url))
                    {
                        using (var downloadResponse = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                        {
                            downloadResponse.EnsureSuccessStatusCode();

                            var totalBytes = downloadResponse.Content.Headers.ContentLength ?? 0;
                            var canReportProgress = totalBytes > 0;

                            using var contentStream = await downloadResponse.Content.ReadAsStreamAsync();
                            using var fs = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                            var buffer = new byte[8192];
                            long totalRead = 0;
                            int bytesRead;

                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fs.WriteAsync(buffer, 0, bytesRead);
                                totalRead += bytesRead;

                                if (canReportProgress)
                                {
                                    var downloadPercent = (double)totalRead / totalBytes;
                                    game.DownloadProgress = 10 + (downloadPercent * 80);
                                }
                            }
                        }
                    }

                    game.DownloadProgress = 90;
                    game.Status = GameStatus.Installing;
                    game.DownloadProgress = 95;

                    await GameInstallationService.InstallOrUpdateGameAsync(
                        downloadPath,
                        gamePath,
                        asset.name,
                        latestRelease.tag_name,
                        GetInstallationOptions()).ConfigureAwait(false);

                    game.DownloadProgress = 100;
                    await Task.Delay(500);

                    game.InstalledVersion = latestRelease.tag_name;
                    if (string.IsNullOrWhiteSpace(game.LatestVersion) || GameInfo.IsNewerVersion(latestRelease.tag_name, game.LatestVersion))
                    {
                        game.LatestVersion = latestRelease.tag_name;
                    }
                    game.Status = GameStatus.Installed;
                    game.DownloadProgress = 0;
                    game.SelectedDownload = null;
                    game.AvailableDownloads = null;
                }
                finally
                {
                    bool wasSingleExecutable = asset.name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                                asset.name.EndsWith(".appimage", StringComparison.OrdinalIgnoreCase);

                    if (!wasSingleExecutable && File.Exists(downloadPath))
                    {
                        try { File.Delete(downloadPath); }
                        catch (Exception ex) { Log.Debug($"Failed to delete temp file {downloadPath}: {ex.Message}"); }
                    }
                }

                if (game.GameManager != null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        game.GameManager.OnPropertyChanged(nameof(GameManager.Games));
                    });
                }
            }
            catch (HttpRequestException ex)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (ex.Message.Contains("403") || ex.Message.ToLower().Contains("rate limit"))
                    {
                        await ShowRateLimitErrorAsync();
                    }
                    else
                    {
                        await ShowMessageBoxAsync($"Network error installing {game.Name}: {ex.Message}\n\nPlease check your internet connection.", "Network Error");
                    }
                });
                game.Status = GameStatus.NotInstalled;
                game.DownloadProgress = 0;
            }
            catch (UnauthorizedAccessException ex)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync($"Permission error installing {game.Name}: {ex.Message}\n\nPlease check folder permissions.", "Permission Error");
                });
                game.Status = GameStatus.NotInstalled;
                game.DownloadProgress = 0;
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await ShowMessageBoxAsync($"Error installing {game.Name}: {ex.Message}", "Installation Error");
                });
                game.Status = GameStatus.NotInstalled;
                game.DownloadProgress = 0;
            }
        }

        // ================================================================
        //  Launch
        // ================================================================

        public async Task LaunchAsync(GameInfo game, string gamesFolder)
        {
            if (string.IsNullOrEmpty(game.FolderName))
            {
                await ShowMessageBoxAsync("Cannot launch game: folder name is not configured.", "Configuration Error");
                return;
            }

            try
            {
                string gamePath = game.GetInstallPath(gamesFolder);

                if (!Directory.Exists(gamePath))
                {
                    await ShowMessageBoxAsync($"App directory not found: {gamePath}", "Directory Not Found");
                    return;
                }

                GameInstallationService.EnsureExecutableAtRoot(gamePath, GetInstallationOptions());

                var executables = GameInstallationService.FindExecutableCandidates(
                    gamePath,
                    SearchOption.TopDirectoryOnly,
                    GetInstallationOptions(),
                    out bool needsWine);

                if (executables.Count == 0)
                {
                    executables = GameInstallationService.FindExecutableCandidates(
                        gamePath,
                        SearchOption.AllDirectories,
                        GetInstallationOptions(),
                        out needsWine);
                }

                if (executables.Count == 0)
                {
                    await ShowMessageBoxAsync(
                        $"No executable found for {game.Name} in:\n{gamePath}\n\nThe game may not have installed correctly.",
                        "Executable Not Found");
                    return;
                }

                var settings = AppSettings.Load();

                if (needsWine && !IsWindowsRunnerAvailable(settings))
                {
                    await ShowMessageBoxAsync(
                        "Only a Windows executable was found, but no Linux Windows-runner is configured or detected.\n\n" +
                        "Install Wine/Proton or set a custom command in Settings to launch Windows apps.",
                        "Windows Runner Not Found");
                    return;
                }

                game.AvailableExecutables = executables;

                string? executablePath = null;

                if (string.IsNullOrEmpty(game.SelectedExecutable))
                {
                    game.SelectedExecutable = _cacheService.LoadSelectedExecutable(game, gamesFolder);
                }

                if (executables.Count > 1 && (string.IsNullOrEmpty(game.SelectedExecutable) || !executables.Contains(game.SelectedExecutable)))
                {
                    game.SelectedExecutable = null;
                    return;
                }

                executablePath = !string.IsNullOrEmpty(game.SelectedExecutable) && executables.Contains(game.SelectedExecutable)
                    ? game.SelectedExecutable
                    : executables[0];

                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                    !executablePath.EndsWith(".app") && !needsWine)
                {
                    await MakeExecutableAsync(executablePath);
                }

                var startInfo = new ProcessStartInfo();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && executablePath.EndsWith(".app"))
                {
                    startInfo.FileName = "open";
                    startInfo.Arguments = $"\"{executablePath}\"";
                    startInfo.UseShellExecute = false;
                    startInfo.WorkingDirectory = gamePath;
                }
                else if (needsWine && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    var runnerCommand = GetWindowsRunnerCommand(settings, executablePath, gamePath);
                    if (runnerCommand == null)
                    {
                        await ShowMessageBoxAsync("A Linux Windows-runner was detected earlier but is no longer available.", "Windows Runner Error");
                        return;
                    }

                    startInfo.UseShellExecute = false;
                    startInfo.WorkingDirectory = gamePath;
                    startInfo.FileName = runnerCommand.FileName;

                    foreach (var argument in runnerCommand.Arguments)
                    {
                        startInfo.ArgumentList.Add(argument);
                    }

                    foreach (var variable in runnerCommand.EnvironmentVariables)
                    {
                        startInfo.Environment[variable.Key] = variable.Value;
                    }
                }
                else
                {
                    startInfo.FileName = executablePath;
                    startInfo.WorkingDirectory = Path.GetDirectoryName(executablePath) ?? gamePath;
                    startInfo.UseShellExecute = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                }

                _cacheService.UpdateLastPlayedTime(game, gamesFolder);

                var gameProcess = Process.Start(startInfo);
                game.NotifyGameProcessStarted(gameProcess);

                if (game.GameManager != null)
                {
                    game.GameManager.OnPropertyChanged(nameof(GameManager.Games));
                }
            }
            catch (Exception ex)
            {
                await ShowMessageBoxAsync($"Error launching {game.Name}: {ex.Message}", "Launch Error");
            }
        }

        // ================================================================
        //  Static utilities
        // ================================================================

        public static string? GetPlatformIcon(string assetName)
        {
            var assetNameLower = assetName.ToLowerInvariant();

            if (HasAnyOf(assetNameLower, "windows", "win64", "win32", "win-x64", "win-x86", "-win.", "_win.", ".exe", ".msi") ||
                System.Text.RegularExpressions.Regex.IsMatch(assetNameLower, @"[_-]win[_-]|[_-]win\d|^win[_-]"))
            {
                if (!HasAnyOf(assetNameLower, "linux", "macos", "darwin", ".deb", ".rpm", ".appimage", ".dmg"))
                    return "avares://GithubLauncher/Assets/Icons/platform_win.png";
            }

            if (HasAnyOf(assetNameLower, "macos", "osx", "darwin", ".dmg", ".pkg") ||
                (assetNameLower.Contains("mac") && !assetNameLower.Contains("machin")))
            {
                if (!HasAnyOf(assetNameLower, "linux", "windows", "win32", "win64", ".exe"))
                    return "avares://GithubLauncher/Assets/Icons/platform_mac.png";
            }

            if (HasAnyOf(assetNameLower, "linux", ".appimage", ".deb", ".rpm", "tar.gz", "tar.xz"))
            {
                if (!HasAnyOf(assetNameLower, "windows", "win32", "win64", "macos", "osx", "darwin", ".exe", ".dmg"))
                    return "avares://GithubLauncher/Assets/Icons/platform_lin.png";
            }

            return null;
        }

        public static bool MatchesPlatform(string assetName, string platformIdentifier)
        {
            return PlatformAssetMatcher.MatchesPlatform(assetName, platformIdentifier);
        }

        public static string GetPlatformIdentifier(AppSettings settings)
        {
            return PlatformAssetMatcher.GetPlatformIdentifier(settings.Platform);
        }

        internal static GameInstallationOptions GetInstallationOptions()
        {
            return new GameInstallationOptions
            {
                Log = _ => { }
            };
        }

        private static bool HasAnyOf(string input, params string[] substrings)
        {
            foreach (var substring in substrings)
            {
                if (input.Contains(substring))
                    return true;
            }
            return false;
        }

        // ================================================================
        //  Executable helpers (delegates to Core library)
        // ================================================================

        internal static void EnsureExecutableAtRoot(string gamePath)
        {
            GameInstallationService.EnsureExecutableAtRoot(gamePath, GetInstallationOptions());
        }

        internal static List<string> GetExecutableCandidates(string gamePath, SearchOption searchOption, out bool needsWine)
        {
            return GameInstallationService.FindExecutableCandidates(
                gamePath,
                searchOption,
                GetInstallationOptions(),
                out needsWine);
        }

        // ================================================================
        //  Asset helpers
        // ================================================================

        private static List<GitHubAsset> GetDownloadableAssets(GitHubRelease release)
        {
            return GitHubReleaseService.GetDownloadableAssets(release);
        }

        // ================================================================
        //  GitHub token
        // ================================================================

        private string GetGitHubApiToken()
        {
            try
            {
                return SecretStore.ReadToken();
            }
            catch
            {
                return string.Empty;
            }
        }

        // ================================================================
        //  Linux Windows runner helpers
        // ================================================================

        private sealed class RunnerCommandSpec
        {
            public required string FileName { get; init; }
            public required List<string> Arguments { get; init; }
            public Dictionary<string, string> EnvironmentVariables { get; init; } = new(StringComparer.Ordinal);
        }

        private sealed class ProtonInstallation
        {
            public required string ProtonExecutable { get; init; }
            public required string SteamRoot { get; init; }
        }

        private static readonly Dictionary<string, Func<string, string, string>> RunnerPlaceholderResolvers = new(StringComparer.Ordinal)
        {
            ["{exe}"] = (executablePath, _) => executablePath,
            ["{gamePath}"] = (_, gamePath) => gamePath,
            ["{exeDir}"] = (executablePath, gamePath) => Path.GetDirectoryName(executablePath) ?? gamePath
        };

        private static async Task MakeExecutableAsync(string executablePath)
        {
            try
            {
                var chmodProcess = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{executablePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(chmodProcess);
                if (process != null)
                {
                    await process.WaitForExitAsync();

                    if (process.ExitCode != 0)
                    {
                        string errorOutput = await process.StandardError.ReadToEndAsync();
                        Log.Debug($"chmod failed for {executablePath}: {errorOutput}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to make file executable {executablePath}: {ex.Message}");
            }
        }

        private static bool IsWindowsRunnerAvailable(AppSettings? settings = null)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return false;

            if (!string.IsNullOrWhiteSpace(settings?.LinuxWindowsLaunchCommand))
                return true;

            return IsWineOrProtonAvailable();
        }

        private static bool IsWineOrProtonAvailable()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return false;

            if (IsCommandAvailable("wine") || IsCommandAvailable("wine64"))
                return true;

            foreach (var protonInstallation in GetProtonInstallations())
            {
                if (File.Exists(protonInstallation.ProtonExecutable))
                    return true;
            }

            return false;
        }

        private static RunnerCommandSpec? GetWindowsRunnerCommand(AppSettings settings, string executablePath, string gamePath)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return null;

            if (!string.IsNullOrWhiteSpace(settings.LinuxWindowsLaunchCommand))
            {
                return BuildWindowsRunnerCommand(settings.LinuxWindowsLaunchCommand, executablePath, gamePath);
            }

            if (IsCommandAvailable("wine64"))
                return BuildWindowsRunnerCommand("wine64 {exe}", executablePath, gamePath);

            if (IsCommandAvailable("wine"))
                return BuildWindowsRunnerCommand("wine {exe}", executablePath, gamePath);

            foreach (var protonInstallation in GetProtonInstallations())
            {
                if (!File.Exists(protonInstallation.ProtonExecutable))
                    continue;

                var compatDataPath = GetProtonCompatDataPath(gamePath);
                var compatAppId = GetStableCompatAppId(executablePath);
                Directory.CreateDirectory(compatDataPath);

                return new RunnerCommandSpec
                {
                    FileName = protonInstallation.ProtonExecutable,
                    Arguments = ["waitforexitandrun", executablePath],
                    EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = protonInstallation.SteamRoot,
                        ["STEAM_COMPAT_DATA_PATH"] = compatDataPath,
                        ["STEAM_COMPAT_APP_ID"] = compatAppId,
                        ["SteamAppId"] = compatAppId,
                        ["SteamGameId"] = compatAppId
                    }
                };
            }

            return null;
        }

        private static RunnerCommandSpec BuildWindowsRunnerCommand(string commandTemplate, string executablePath, string gamePath)
        {
            var resolvedCommand = commandTemplate.Trim();

            if (!resolvedCommand.Contains("{exe}", StringComparison.Ordinal) &&
                !resolvedCommand.Contains("{gamePath}", StringComparison.Ordinal) &&
                !resolvedCommand.Contains("{exeDir}", StringComparison.Ordinal))
            {
                resolvedCommand += " {exe}";
            }

            var tokens = SplitRunnerCommand(resolvedCommand);
            if (tokens.Count == 0 || string.IsNullOrWhiteSpace(tokens[0]))
            {
                throw new InvalidOperationException("The Linux Windows-runner command is empty.");
            }

            var resolvedTokens = tokens
                .Select(token => ReplaceRunnerPlaceholders(token, executablePath, gamePath))
                .ToList();

            return new RunnerCommandSpec
            {
                FileName = resolvedTokens[0],
                Arguments = resolvedTokens.Skip(1).ToList()
            };
        }

        private static List<string> SplitRunnerCommand(string command)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            bool inSingleQuotes = false;
            bool inDoubleQuotes = false;
            bool escaping = false;

            foreach (var character in command)
            {
                if (escaping)
                {
                    current.Append(character);
                    escaping = false;
                    continue;
                }

                if (character == '\\' && !inSingleQuotes)
                {
                    escaping = true;
                    continue;
                }

                if (character == '"' && !inSingleQuotes)
                {
                    inDoubleQuotes = !inDoubleQuotes;
                    continue;
                }

                if (character == '\'' && !inDoubleQuotes)
                {
                    inSingleQuotes = !inSingleQuotes;
                    continue;
                }

                if (char.IsWhiteSpace(character) && !inSingleQuotes && !inDoubleQuotes)
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }

                current.Append(character);
            }

            if (escaping || inSingleQuotes || inDoubleQuotes)
            {
                throw new InvalidOperationException("The Linux Windows-runner command contains an unmatched quote or trailing escape character.");
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }

            return tokens;
        }

        private static string ReplaceRunnerPlaceholders(string token, string executablePath, string gamePath)
        {
            var resolvedToken = token;

            foreach (var placeholder in RunnerPlaceholderResolvers)
            {
                if (resolvedToken.Contains(placeholder.Key, StringComparison.Ordinal))
                {
                    resolvedToken = resolvedToken.Replace(
                        placeholder.Key,
                        placeholder.Value(executablePath, gamePath),
                        StringComparison.Ordinal);
                }
            }

            return resolvedToken;
        }

        private static IEnumerable<ProtonInstallation> GetProtonInstallations()
        {
            foreach (var steamRoot in GetSteamRoots())
            {
                var commonPath = Path.Combine(steamRoot, "steamapps", "common");
                foreach (var protonDir in GetExistingDirectories(commonPath, "Proton*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var protonExe = Path.Combine(protonDir, "proton");
                    if (File.Exists(protonExe))
                    {
                        yield return new ProtonInstallation
                        {
                            ProtonExecutable = protonExe,
                            SteamRoot = steamRoot
                        };
                    }
                }

                var compatibilityToolsPath = Path.Combine(steamRoot, "compatibilitytools.d");
                foreach (var protonDir in GetExistingDirectories(compatibilityToolsPath, "*Proton*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var protonExe = Path.Combine(protonDir, "proton");
                    if (File.Exists(protonExe))
                    {
                        yield return new ProtonInstallation
                        {
                            ProtonExecutable = protonExe,
                            SteamRoot = steamRoot
                        };
                    }
                }
            }
        }

        private static IEnumerable<string> GetSteamRoots()
        {
            var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var steamRoots = new[]
            {
                Path.Combine(homePath, ".steam", "root"),
                Path.Combine(homePath, ".steam", "steam"),
                Path.Combine(homePath, ".local", "share", "Steam"),
                Path.Combine(homePath, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam")
            };

            return steamRoots
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> GetExistingDirectories(string parentPath, string searchPattern)
        {
            if (!Directory.Exists(parentPath))
                return [];

            try
            {
                return Directory.GetDirectories(parentPath, searchPattern, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return [];
            }
        }

        private static string GetProtonCompatDataPath(string gamePath)
        {
            return Path.Combine(gamePath, ".steam-compat-data");
        }

        private static string GetStableCompatAppId(string executablePath)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var character in executablePath)
                {
                    hash ^= character;
                    hash *= 16777619;
                }

                return (hash & 0x7FFFFFFF).ToString();
            }
        }

        private static bool IsCommandAvailable(string command)
        {
            try
            {
                var process = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = command,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(process);
                if (proc != null)
                {
                    proc.WaitForExit();
                    return proc.ExitCode == 0;
                }
            }
            catch { }

            return false;
        }

        // ================================================================
        //  UI helpers (message boxes)
        // ================================================================

        /// <summary>
        /// Shows a simple message box dialog.
        /// </summary>
        public static async Task ShowMessageBoxAsync(string message, string title)
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow != null)
                {
                    var messageBox = new Window
                    {
                        Title = title,
                        Width = 800,
                        Height = 400,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new StackPanel
                        {
                            Margin = new Thickness(20),
                            Children =
                            {
                                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) },
                                new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Center }
                            }
                        }
                    };

                    if (((StackPanel)messageBox.Content).Children[1] is Button okButton)
                    {
                        okButton.Click += (s, e) => messageBox.Close();
                    }

                    await messageBox.ShowDialog(desktop.MainWindow);
                }
                else
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"ERROR: {title}");
                    Console.ResetColor();
                    Console.WriteLine(message);
                    Console.WriteLine();
                }
            });
        }

        private static async Task<bool> ShowWineNotFoundWarning()
        {
            bool userChoice = false;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow != null)
                {
                    var messageBox = new Window
                    {
                        Title = "Windows Runner Not Found",
                        Width = 500,
                        Height = 220,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new StackPanel
                        {
                            Margin = new Thickness(20),
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = "This game requires a Linux Windows-runner to launch, but none was detected.\n\n" +
                                           "Install Wine/Proton or set a custom command in Settings for Bottles or another launcher.\n\n" +
                                           "Do you want to download anyway? The game will not launch without a configured runner.",
                                    TextWrapping = TextWrapping.Wrap,
                                    Margin = new Thickness(0, 0, 0, 20)
                                },
                                new StackPanel
                                {
                                    Orientation = Orientation.Horizontal,
                                    HorizontalAlignment = HorizontalAlignment.Center,
                                    Spacing = 10,
                                    Children =
                                    {
                                        new Button { Content = "Download Anyway", Width = 140 },
                                        new Button { Content = "Cancel", Width = 100 }
                                    }
                                }
                            }
                        }
                    };

                    if (((StackPanel)messageBox.Content).Children[1] is StackPanel buttonPanel &&
                        buttonPanel.Children[0] is Button yesButton &&
                        buttonPanel.Children[1] is Button noButton)
                    {
                        yesButton.Click += (s, e) => { userChoice = true; messageBox.Close(); };
                        noButton.Click += (s, e) => { userChoice = false; messageBox.Close(); };
                    }

                    await messageBox.ShowDialog(desktop.MainWindow);
                }
            });

            return userChoice;
        }

        private static async Task<bool> ShowWineDownloadWarning()
        {
            bool userChoice = false;

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow != null)
                {
                    var messageBox = new Window
                    {
                        Title = "Windows Runner Required",
                        Width = 500,
                        Height = 200,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new StackPanel
                        {
                            Margin = new Thickness(20),
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = "This game requires a Linux Windows-runner to launch. A compatible runner was detected or configured and will be used.\n\nWant to download?",
                                    TextWrapping = TextWrapping.Wrap,
                                    Margin = new Thickness(0, 0, 0, 20)
                                },
                                new StackPanel
                                {
                                    Orientation = Orientation.Horizontal,
                                    HorizontalAlignment = HorizontalAlignment.Center,
                                    Spacing = 10,
                                    Children =
                                    {
                                        new Button { Content = "Yes", Width = 100 },
                                        new Button { Content = "No", Width = 100 }
                                    }
                                }
                            }
                        }
                    };

                    if (((StackPanel)messageBox.Content).Children[1] is StackPanel buttonPanel &&
                        buttonPanel.Children[0] is Button yesButton &&
                        buttonPanel.Children[1] is Button noButton)
                    {
                        yesButton.Click += (s, e) => { userChoice = true; messageBox.Close(); };
                        noButton.Click += (s, e) => { userChoice = false; messageBox.Close(); };
                    }

                    await messageBox.ShowDialog(desktop.MainWindow);
                }
            });

            return userChoice;
        }

        private static async Task ShowRateLimitErrorAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                    desktop.MainWindow != null)
                {
                    var hyperlinkText = new TextBlock
                    {
                        Text = "https://github.com/settings/tokens",
                        Foreground = new SolidColorBrush(Color.FromRgb(0, 122, 255)),
                        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(10, 0, 0, 0)
                    };

                    hyperlinkText.PointerPressed += (s, e) =>
                    {
                        try
                        {
                            var url = "https://github.com/settings/tokens";
                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                                Process.Start("xdg-open", url);
                            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                                Process.Start("open", url);
                        }
                        catch (Exception ex)
                        {
                            Log.Debug($"Failed to open URL: {ex.Message}");
                        }
                    };

                    var okButton = new Button
                    {
                        Content = "OK",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 10, 0, 0),
                        MinWidth = 100
                    };

                    var messageBox = new Window
                    {
                        Title = "Rate Limit Exceeded",
                        Width = 600,
                        Height = 450,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        Content = new ScrollViewer
                        {
                            Content = new StackPanel
                            {
                                Margin = new Thickness(20),
                                Spacing = 15,
                                Children =
                                {
                                    new TextBlock
                                    {
                                        Text = "GitHub API rate limit exceeded.",
                                        FontWeight = FontWeight.Bold,
                                        FontSize = 16,
                                        TextWrapping = TextWrapping.Wrap
                                    },
                                    new TextBlock
                                    {
                                        Text = "GitHub limits anonymous requests to 60 per hour. The limit resets one hour after depletion.",
                                        TextWrapping = TextWrapping.Wrap
                                    },
                                    new TextBlock
                                    {
                                        Text = "To avoid this, add a GitHub API token in Settings:",
                                        FontWeight = FontWeight.SemiBold,
                                        TextWrapping = TextWrapping.Wrap,
                                        Margin = new Thickness(0, 10, 0, 0)
                                    },
                                    new TextBlock { Text = "1. Click the link below to create a token:", TextWrapping = TextWrapping.Wrap },
                                    hyperlinkText,
                                    new TextBlock { Text = "2. Click 'Generate new token (classic)'", TextWrapping = TextWrapping.Wrap },
                                    new TextBlock { Text = "3. Give it a name (no special permissions needed)", TextWrapping = TextWrapping.Wrap },
                                    new TextBlock { Text = "4. Click 'Generate token' at the bottom", TextWrapping = TextWrapping.Wrap },
                                    new TextBlock { Text = "5. Copy the token and paste it in the launcher Settings", TextWrapping = TextWrapping.Wrap },
                                    new TextBlock
                                    {
                                        Text = "⚠️ Do not share your token with anyone!",
                                        Foreground = new SolidColorBrush(Color.FromRgb(255, 149, 0)),
                                        FontWeight = FontWeight.Bold,
                                        TextWrapping = TextWrapping.Wrap,
                                        Margin = new Thickness(0, 10, 0, 0)
                                    },
                                    okButton
                                }
                            }
                        }
                    };

                    okButton.Click += (s, e) => messageBox.Close();

                    await messageBox.ShowDialog(desktop.MainWindow);
                }
            });
        }
    }
}
