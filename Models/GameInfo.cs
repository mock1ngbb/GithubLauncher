using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GitHubLauncher.Core.Models;
using GitHubLauncher.Core.Services;
using GithubLauncher.Services;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace GithubLauncher.Models
{
    /// <summary>
    /// Domain model for a game/app entry. Holds pure data and computed
    /// view-state properties. GitHub API interaction, file I/O, and
    /// download/install/launch logic have been extracted into
    /// <c>GameApiService</c> and <c>GameCacheService</c>.
    /// </summary>
    public class GameInfo : INotifyPropertyChanged, IDisposable
    {
        private const string DefaultInstalledVersion = "v0.0.0";
        internal Action<Process?>? GameProcessStarted;
        private string? _latestVersion;
        private string? _installedVersion;
        private string? _preferredVersion;
        private string? _skippedUpdateVersion;
        private GameStatus _status = GameStatus.NotInstalled;
        private bool _isLoading;
        public GameManager? GameManager { get; set; }

        /// <summary>Cached latest release, set by GameApiService.</summary>
        internal GitHubRelease? CachedRelease { get; set; }

        public string? Name { get; set; }
        public string? Repository { get; set; }
        public string? FolderName { get; set; }
        public string? InstallPath { get; set; }
        public string? GameIconUrl { get; set; }
        public bool IsExperimental { get; set; }
        public bool IsCustom { get; set; }

        private string? _customIconPath;
        public string? CustomIconPath
        {
            get => _customIconPath;
            set
            {
                if (_customIconPath != value)
                {
                    _customIconPath = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IconUrl));
                    OnPropertyChanged(nameof(HasCustomIcon));
                }
            }
        }

        /// <summary>Cached default icon path, set by GameCacheService.</summary>
        internal string? CachedDefaultIconPath { get; set; }

        public bool HasCustomIcon => !string.IsNullOrEmpty(CustomIconPath) && File.Exists(CustomIconPath);

        public string IconUrl
        {
            get
            {
                if (!string.IsNullOrEmpty(CustomIconPath) && File.Exists(CustomIconPath))
                    return CustomIconPath;

                if (!string.IsNullOrEmpty(CachedDefaultIconPath) && File.Exists(CachedDefaultIconPath))
                    return CachedDefaultIconPath;

                return DefaultIconUrl;
            }
        }

        public string DefaultIconUrl
        {
            get
            {
                if (!string.IsNullOrEmpty(GameIconUrl))
                    return GameIconUrl;
                return "/Assets/DefaultGame.png";
            }
        }

        public bool HasStoredExecutable
        {
            get
            {
                if (string.IsNullOrEmpty(FolderName) || GameManager == null)
                    return false;

                try
                {
                    var gamePath = GetInstallPath(GameManager.GamesFolder);
                    if (string.IsNullOrWhiteSpace(gamePath))
                        return false;

                    var selectedExePath = Path.Combine(gamePath, "selected_executable.txt");
                    return File.Exists(selectedExePath);
                }
                catch
                {
                    return false;
                }
            }
        }

        private List<string>? _availableExecutables;
        public List<string>? AvailableExecutables
        {
            get => _availableExecutables;
            set
            {
                if (_availableExecutables != value)
                {
                    _availableExecutables = value;
                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(HasMultipleExecutables));
                    DispatchPropertyChanged(nameof(HasExecutableChoice));
                    DispatchPropertyChanged(nameof(CanLaunchOptions));
                }
            }
        }

        private string? _selectedExecutable;
        public string? SelectedExecutable
        {
            get => _selectedExecutable;
            set
            {
                if (_selectedExecutable != value)
                {
                    _selectedExecutable = value;
                    DispatchPropertyChanged();
                }
            }
        }

        public bool HasMultipleExecutables => AvailableExecutables?.Count > 1;

        public bool HasExecutableChoice
        {
            get
            {
                if (!IsInstalled || string.IsNullOrWhiteSpace(FolderName) || GameManager == null)
                    return false;

                if (HasMultipleExecutables)
                    return true;

                try
                {
                    var gamePath = GetInstallPath(GameManager.GamesFolder);
                    if (!Directory.Exists(gamePath))
                        return false;

                    var executables = GitHubLauncher.Core.Services.GameInstallationService.FindExecutableCandidates(
                        gamePath,
                        SearchOption.TopDirectoryOnly,
                        GetInstallationOptions(),
                        out _);
                    if (executables.Count <= 1)
                    {
                        executables = GitHubLauncher.Core.Services.GameInstallationService.FindExecutableCandidates(
                            gamePath,
                            SearchOption.AllDirectories,
                            GetInstallationOptions(),
                            out _);
                    }

                    return executables.Count > 1;
                }
                catch
                {
                    return false;
                }
            }
        }

        private List<GitHubAsset>? _availableDownloads;
        public List<GitHubAsset>? AvailableDownloads
        {
            get => _availableDownloads;
            set
            {
                if (_availableDownloads != value)
                {
                    _availableDownloads = value;
                    DispatchPropertyChanged();
                }
            }
        }

        private GitHubAsset? _selectedDownload;
        public GitHubAsset? SelectedDownload
        {
            get => _selectedDownload;
            set
            {
                if (_selectedDownload != value)
                {
                    _selectedDownload = value;
                    DispatchPropertyChanged();
                }
            }
        }

        public bool HasMultipleDownloads => AvailableDownloads?.Count > 1;

        public bool IsInstalled => Status == GameStatus.Installed || Status == GameStatus.UpdateAvailable;
        public bool CanLaunch => Status == GameStatus.Installed;
        public bool CanDownload => Status == GameStatus.NotInstalled;
        public bool CanLocateInstall => Status == GameStatus.NotInstalled;
        public bool CanUpdate => Status == GameStatus.UpdateAvailable;
        public bool CanSkipUpdate => Status == GameStatus.UpdateAvailable;
        public bool CanChangeVersion => IsInstalled && !string.IsNullOrWhiteSpace(Repository);
        public bool CanVersionOptions => CanSkipUpdate || CanChangeVersion || IsInstalled;
        public bool CanLaunchOptions => HasExecutableChoice || IsInstalled;
        public bool CanInfoOptions => !string.IsNullOrWhiteSpace(Repository);
        public bool HasPreferredVersion => !string.IsNullOrWhiteSpace(PreferredVersion);

        public string? LatestVersion
        {
            get => _latestVersion;
            set
            {
                if (_latestVersion != value)
                {
                    _latestVersion = value;

                    if (AreVersionsEquivalent(_preferredVersion, _latestVersion))
                    {
                        _preferredVersion = null;
                        DispatchPropertyChanged(nameof(PreferredVersion));
                        DispatchPropertyChanged(nameof(HasPreferredVersion));
                    }

                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(StatusText));
                }
            }
        }

        public string? InstalledVersion
        {
            get => _installedVersion;
            set
            {
                if (_installedVersion != value)
                {
                    _installedVersion = value;
                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(StatusText));
                }
            }
        }

        public string? PreferredVersion
        {
            get => _preferredVersion;
            set
            {
                if (AreVersionsEquivalent(value, LatestVersion))
                {
                    value = null;
                }

                if (_preferredVersion != value)
                {
                    _preferredVersion = value;
                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(HasPreferredVersion));
                }
            }
        }

        public string? SkippedUpdateVersion
        {
            get => _skippedUpdateVersion;
            set
            {
                if (_skippedUpdateVersion != value)
                {
                    _skippedUpdateVersion = value;
                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(StatusText));
                }
            }
        }

        public GameStatus Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(ButtonText));
                    DispatchPropertyChanged(nameof(ButtonImage));
                    DispatchPropertyChanged(nameof(ButtonColor));
                    DispatchPropertyChanged(nameof(StatusText));
                    DispatchPropertyChanged(nameof(IsInstalled));
                    DispatchPropertyChanged(nameof(CanLaunch));
                    DispatchPropertyChanged(nameof(CanDownload));
                    DispatchPropertyChanged(nameof(CanLocateInstall));
                    DispatchPropertyChanged(nameof(CanUpdate));
                    DispatchPropertyChanged(nameof(CanSkipUpdate));
                    DispatchPropertyChanged(nameof(CanChangeVersion));
                    DispatchPropertyChanged(nameof(CanVersionOptions));
                    DispatchPropertyChanged(nameof(HasExecutableChoice));
                    DispatchPropertyChanged(nameof(CanLaunchOptions));
                }
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    DispatchPropertyChanged();
                }
            }
        }

        public string ButtonText
        {
            get
            {
                return Status switch
                {
                    GameStatus.NotInstalled => "Download",
                    GameStatus.Installed => "Launch",
                    GameStatus.UpdateAvailable => "Update",
                    GameStatus.Downloading => "Downloading...",
                    GameStatus.Installing => "Installing...",
                    _ => "Download"
                };
            }
        }

        private Bitmap? _buttonImageCache;
        private string? _lastImagePath;

        public Bitmap ButtonImage
        {
            get
            {
                var imagePath = Status switch
                {
                    GameStatus.NotInstalled => "avares://GithubLauncher/Assets/Icons/button_download.png",
                    GameStatus.Installed => "avares://GithubLauncher/Assets/Icons/button_launch.png",
                    GameStatus.UpdateAvailable => "avares://GithubLauncher/Assets/Icons/button_update.png",
                    GameStatus.Downloading => "avares://GithubLauncher/Assets/Icons/button_loading.png",
                    GameStatus.Installing => "avares://GithubLauncher/Assets/Icons/button_loading.png",
                    _ => "avares://GithubLauncher/Assets/Icons/button_loading.png"
                };

                if (_buttonImageCache == null || _lastImagePath != imagePath)
                {
                    _buttonImageCache?.Dispose();
                    _buttonImageCache = new Bitmap(AssetLoader.Open(new Uri(imagePath)));
                    _lastImagePath = imagePath;
                }

                return _buttonImageCache;
            }
        }

        public IBrush ButtonColor
        {
            get
            {
                return Status switch
                {
                    GameStatus.NotInstalled => new SolidColorBrush(Color.FromRgb(0, 122, 255)),
                    GameStatus.Installed => new SolidColorBrush(Color.FromRgb(52, 199, 89)),
                    GameStatus.UpdateAvailable => new SolidColorBrush(Color.FromRgb(255, 149, 0)),
                    GameStatus.Downloading or GameStatus.Installing => new SolidColorBrush(Color.FromRgb(142, 142, 147)),
                    _ => new SolidColorBrush(Color.FromRgb(0, 122, 255))
                };
            }
        }

        public string StatusText
        {
            get
            {
                if (Status == GameStatus.Installed && !string.IsNullOrEmpty(InstalledVersion))
                    return $"Installed: {InstalledVersion}";

                if (Status == GameStatus.UpdateAvailable && !string.IsNullOrEmpty(LatestVersion))
                    return $"Update available!: {InstalledVersion} -> {LatestVersion}";

                return Status switch
                {
                    GameStatus.NotInstalled => "Not installed",
                    GameStatus.Downloading => "Downloading...",
                    GameStatus.Installing => "Installing...",
                    _ => ""
                };
            }
        }

        private double _downloadProgress;
        public double DownloadProgress
        {
            get => _downloadProgress;
            set
            {
                if (_downloadProgress != value)
                {
                    _downloadProgress = value;
                    DispatchPropertyChanged();
                    DispatchPropertyChanged(nameof(IsDownloading));
                    DispatchPropertyChanged(nameof(ProgressBarColor));
                }
            }
        }

        public bool IsDownloading => Status == GameStatus.Downloading || Status == GameStatus.Installing || Status == GameStatus.Updating;

        public IBrush ProgressBarColor
        {
            get
            {
                if (Status == GameStatus.Updating)
                {
                    var progress = DownloadProgress / 100.0;
                    byte r = (byte)(255 - (255 - 52) * progress);
                    byte g = (byte)(149 + (199 - 149) * progress);
                    byte b = (byte)(0 + (89 - 0) * progress);
                    return new SolidColorBrush(Color.FromRgb(r, g, b));
                }
                else
                {
                    var progress = DownloadProgress / 100.0;
                    byte r = (byte)(0 + (52 - 0) * progress);
                    byte g = (byte)(122 + (199 - 122) * progress);
                    byte b = (byte)(255 - (255 - 89) * progress);
                    return new SolidColorBrush(Color.FromRgb(r, g, b));
                }
            }
        }

        public void Dispose()
        {
            _buttonImageCache?.Dispose();
            _buttonImageCache = null;
        }

        public void SetGameManager(GameManager gameManager)
        {
            GameManager = gameManager;
            DispatchPropertyChanged(nameof(HasExecutableChoice));
            DispatchPropertyChanged(nameof(CanLaunchOptions));
        }

        private void DispatchPropertyChanged([CallerMemberName] string propertyName = "")
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                OnPropertyChanged(propertyName);
            }
            else
            {
                Dispatcher.UIThread.InvokeAsync(() => OnPropertyChanged(propertyName));
            }
        }

        public string GetInstallPath(string gamesFolder)
        {
            if (!string.IsNullOrWhiteSpace(InstallPath))
                return InstallPath;

            return string.IsNullOrWhiteSpace(FolderName)
                ? string.Empty
                : Path.Combine(gamesFolder, FolderName);
        }

        // ---- Version helpers (pure, no I/O) ----

        internal static string NormalizeVersionString(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return "0.0.0";

            var normalized = version.Trim().TrimStart('v', 'V');
            var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();

            while (segments.Count < 3)
                segments.Add("0");

            return string.Join(".", segments.Take(4));
        }

        internal static bool IsNewerVersion(string candidateVersion, string baselineVersion)
        {
            try
            {
                var candidate = new Version(NormalizeVersionString(candidateVersion));
                var baseline = new Version(NormalizeVersionString(baselineVersion));
                return candidate.CompareTo(baseline) > 0;
            }
            catch
            {
                return !candidateVersion.TrimStart('v', 'V').Equals(
                    baselineVersion.TrimStart('v', 'V'),
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        internal static bool AreVersionsEquivalent(string? firstVersion, string? secondVersion)
        {
            if (string.IsNullOrWhiteSpace(firstVersion) || string.IsNullOrWhiteSpace(secondVersion))
                return false;

            try
            {
                return new Version(NormalizeVersionString(firstVersion))
                    .Equals(new Version(NormalizeVersionString(secondVersion)));
            }
            catch
            {
                return firstVersion.TrimStart('v', 'V').Trim()
                    .Equals(secondVersion.TrimStart('v', 'V').Trim(), StringComparison.OrdinalIgnoreCase);
            }
        }

        private bool ShouldSuggestUpdate()
        {
            if (string.IsNullOrWhiteSpace(LatestVersion) ||
                string.IsNullOrWhiteSpace(InstalledVersion) ||
                InstalledVersion == "Unknown")
            {
                return false;
            }

            if (!IsNewerVersion(LatestVersion, InstalledVersion))
                return false;

            if (!string.IsNullOrWhiteSpace(SkippedUpdateVersion) &&
                !IsNewerVersion(LatestVersion, SkippedUpdateVersion))
            {
                return false;
            }

            return true;
        }

        public void RefreshInstalledStatus()
        {
            if (ShouldSuggestUpdate())
            {
                Status = GameStatus.UpdateAvailable;
            }
            else if (Status != GameStatus.Downloading && Status != GameStatus.Installing && !string.IsNullOrWhiteSpace(InstalledVersion))
            {
                Status = GameStatus.Installed;
            }
        }

        public void SetVersionPreferences(string? preferredVersion, string? skippedUpdateVersion)
        {
            PreferredVersion = preferredVersion;
            SkippedUpdateVersion = skippedUpdateVersion;
            RefreshInstalledStatus();
        }

        public void SkipLatestUpdate()
        {
            if (string.IsNullOrWhiteSpace(LatestVersion))
                return;

            var effectiveInstalledVersion = InstalledVersion;
            if (string.IsNullOrWhiteSpace(effectiveInstalledVersion) ||
                effectiveInstalledVersion == "Unknown" ||
                AreVersionsEquivalent(effectiveInstalledVersion, DefaultInstalledVersion))
            {
                effectiveInstalledVersion = LatestVersion;
            }

            if (!string.IsNullOrWhiteSpace(effectiveInstalledVersion))
            {
                InstalledVersion = effectiveInstalledVersion;
                PreferredVersion = effectiveInstalledVersion;
            }

            SkippedUpdateVersion = LatestVersion;
            RefreshInstalledStatus();
        }

        /// <summary>
        /// Selects the release that should be treated as "latest". Prefers the
        /// newest stable release; falls back to the newest pre-release when a
        /// repo publishes no stable releases at all.
        /// </summary>
        /// <summary>
        /// Temporary forwarding: delegates to GameApiService.
        /// Will be removed when MainWindow is updated to use GameApiService directly.
        /// </summary>
        public async Task<List<GitHubRelease>> FetchReleasesAsync(HttpClient httpClient)
        {
            var api = new GameApiService(new GameCacheService());
            return await api.FetchReleasesAsync(this, httpClient);
        }

        public static GitHubRelease? SelectLatestRelease(IReadOnlyList<GitHubRelease> releases)
        {
            if (releases == null || releases.Count == 0)
                return null;

            for (int i = 0; i < releases.Count; i++)
            {
                if (!releases[i].prerelease)
                    return releases[i];
            }

            return releases[0];
        }

        internal static GameInstallationOptions GetInstallationOptions()
        {
            return new GameInstallationOptions
            {
                Log = _ => { }
            };
        }

        // --- Forwarding methods (delegate to extracted services) ---

        public async Task CheckStatusAsync(HttpClient httpClient, string gamesFolder, bool forceUpdateCheck = false)
        {
            var api = new GameApiService(new GameCacheService());
            await api.CheckStatusAsync(this, httpClient, gamesFolder, forceUpdateCheck);
        }

        public async Task ForceUpdateAsync(HttpClient httpClient, string gamesFolder)
        {
            var api = new GameApiService(new GameCacheService());
            await api.ForceUpdateAsync(this, httpClient, gamesFolder);
        }

        public async Task PerformActionAsync(HttpClient httpClient, string gamesFolder, AppSettings settings)
        {
            var api = new GameApiService(new GameCacheService());
            await api.PerformActionAsync(this, httpClient, gamesFolder, settings);
        }


        public async Task InstallReleaseAsync(HttpClient httpClient, string gamesFolder, AppSettings settings, GitHubRelease release, GitHubAsset selectedAsset)
        {
            var api = new GameApiService(new GameCacheService());
            await api.InstallReleaseAsync(this, httpClient, gamesFolder, settings, release, selectedAsset);
        }

        public async Task LaunchAsync(string gamesFolder)
        {
            var api = new GameApiService(new GameCacheService());
            await api.LaunchAsync(this, gamesFolder);
        }

        public async Task LoadAndCacheDefaultIconAsync(string cacheDirectory)
        {
            var cache = new GameCacheService();
            await cache.LoadAndCacheDefaultIconAsync(this, cacheDirectory);
        }

        public void LoadCustomIcon(string cacheDirectory) => new GameCacheService().LoadCustomIcon(this, cacheDirectory);
        public void SaveSelectedExecutable(string executablePath, string gamesFolder) => new GameCacheService().SaveSelectedExecutable(this, executablePath, gamesFolder);
        public string? LoadSelectedExecutable(string gamesFolder) => new GameCacheService().LoadSelectedExecutable(this, gamesFolder);
        public void ClearSelectedExecutable(string gamesFolder) => new GameCacheService().ClearSelectedExecutable(this, gamesFolder);
        public void SetCustomIcon(string sourcePath, string cacheDirectory) => new GameCacheService().SetCustomIcon(this, sourcePath, cacheDirectory);
        public void RemoveCustomIcon() => new GameCacheService().RemoveCustomIcon(this);

        public static string? GetPlatformIcon(string assetName) => GameApiService.GetPlatformIcon(assetName);
        public static bool MatchesPlatform(string assetName, string platformIdentifier) => GameApiService.MatchesPlatform(assetName, platformIdentifier);
        public static string GetPlatformIdentifier(AppSettings settings) => GameApiService.GetPlatformIdentifier(settings);
        public static void EnsureExecutableAtRoot(string gamePath) => GameApiService.EnsureExecutableAtRoot(gamePath);
        public static List<string> GetExecutableCandidates(string gamePath, SearchOption searchOption, out bool needsWine) => GameApiService.GetExecutableCandidates(gamePath, searchOption, out needsWine);

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
