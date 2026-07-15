# GithubLauncher -- Project Conventions

## Build & Run

```bash
# Build (Debug)
dotnet build

# Build (Release)
dotnet build --configuration Release

# Publish (self-contained, platform-specific)
dotnet publish --configuration Release --runtime osx-arm64 --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true
dotnet publish --configuration Release --runtime win-x64 --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true
dotnet publish --configuration Release --runtime linux-x64 --self-contained true
dotnet publish --configuration Release --runtime linux-arm64 --self-contained true

# Run (debug)
dotnet run

# Restore
dotnet restore
```

## Code Conventions

### Nullable Reference Types
- `<Nullable>enable</Nullable>` is set project-wide (`GithubLauncher.csproj`).
- Use `string?` / `T?` for nullable reference types. Mark optional fields and parameters nullable.
- Use `#nullable disable` only as a last resort (never introduce new ones).

### Namespaces
- Use classic namespace blocks: `namespace GithubLauncher.Services { ... }`
- The `App.axaml.cs` file uses file-scoped `namespace GithubLauncher;` -- follow whichever pattern is already in the file.
- Submodule lib uses `GitHubLauncher.Core.Models` and `GitHubLauncher.Core.Services`.

### Naming
- **Classes/Structs/Enums**: `PascalCase` -- e.g. `GameManager`, `HttpClientFactory`, `GameStatus`
- **Methods**: `PascalCase` -- e.g. `CheckStatusAsync`, `PerformActionAsync`
- **Public properties**: `PascalCase` -- e.g. `LatestVersion`, `IsLoading`
- **Private fields**: `_camelCase` with underscore prefix -- e.g. `_latestVersion`, `_httpClient`, `_disposed`
- **Private static fields**: `_camelCase` -- e.g. `_defaultClient`, `_jitter`
- **Constants**: `PascalCase` or `SCREAMING_SNAKE_CASE` (both seen in codebase; follow file-local pattern)
- **Local variables/parameters**: `camelCase` -- e.g. `gamePath`, `versionFile`
- **Interface implementations**: Prefix with capital `I` -- e.g. `INotifyPropertyChanged`, `IDisposable`
- **Async methods**: Suffix with `Async` -- e.g. `LoadAsync`, `FetchReleasesAsync`

### Pattern: INotifyPropertyChanged
- Most ViewModel/Model classes implement `INotifyPropertyChanged`.
- Use the `DispatchPropertyChanged` pattern from `GameInfo.cs` when cross-thread access is possible (check `Dispatcher.UIThread.CheckAccess()`).
- Use `[CallerMemberName]` for the `propertyName` parameter to avoid string literals.

### Pattern: Delegating Handler
- Custom HTTP handlers (retry, rate-limit tracking) extend `DelegatingHandler` and override `SendAsync`.

### Pattern: Singleton
- `HttpClientFactory` is a `static class` with static fields -- shared handler pool.
- `GithubLauncherProfile` is a `sealed class` with a static `Instance` property (singleton).

### Pattern: Using `System.Diagnostics.Debug.WriteLine`
- Used everywhere for non-critical diagnostic output. Avoid `Console.WriteLine` (it is only used in the CLI handler and rare cases).

### Using Directives
- Place `using` statements inside the namespace block (consistent with existing code).
- Group: System.* first, then Avalonia.*, then GitHubLauncher.Core.*, then GithubLauncher.*.

## Architecture

```
GithubLauncher/
  App.axaml / App.axaml.cs         -- Application entry, global styling, update checking
  MainWindow.axaml / .axaml.cs     -- Main UI (large code-behind ~5000+ lines)
  Program.cs                       -- CLI entry + Avalonia desktop bootstrap
  Models/
    GameInfo.cs                    -- App/Game info model + launch/download logic
    UpdateCheckInfo.cs             -- Update check data model
  Services/
    AppSettings.cs                 -- JSON-backed settings (settings.json)
    CLIHandler.cs                  -- CLI commands (--add-steam-shortcut, etc.)
    GameManager.cs                 -- Central orchestrator: loads apps.json, manages GameInfo collection
    GithubLauncherProfile.cs       -- Static app identity/profile (singleton)
    HttpClientFactory.cs           -- Centralized HttpClient with retry + rate-limit tracking
    InputService.cs                -- Gamepad input handling
    ShortcutHelper.cs              -- Desktop shortcut management
  Converters/
    BooleanToStretchConverter.cs   -- Avalonia IValueConverter
    ThicknessConverter.cs          -- Avalonia IValueConverter
  lib/GitHubLauncher.Core/        -- Git submodule: shared models, services, installer
  Assets/                          -- Icons, images, fonts
  .bifrost/                        -- Bifrost deploy manifest + infra config
  .cicada-policy.yml               -- Cicada CI/CD policy (GitHub Actions forbidden)
```

### Technology Stack
- **Runtime**: .NET 9.0
- **UI**: Avalonia 11.3.12 (cross-platform desktop)
- **Themes**: Fluent
- **Fonts**: Inter
- **Packages**: AnimatedImage.Avalonia, AsyncImageLoader.Avalonia, ppy.SDL2-CS, System.Text.Json, NAudio (Windows only)
- **Output Type**: WinExe

### Key Design Decisions
- Centralized `HttpClientFactory` with retry handler (3 retries, exponential backoff + jitter)
- GitHub API rate-limit tracking via `RateLimitTracker` and `RateLimitTrackingHandler`
- GitHub API throttle: `SemaphoreSlim(5, 5)` in `GameManager` to avoid exhausting 60/hr auth-free limit
- `PlatformAssetMatcher` for cross-platform asset download selection

## Commit Message Conventions

From git log:
```
feat: <description>             -- New feature
fix: <description>              -- Bug fix
refactor: <description>          -- Code restructuring
chore: <description>             -- Maintenance, build, dependencies
docs: <description>              -- Documentation only
```

Messages are lowercase, concise (under 70 chars when possible), and use prefix format.

## Testing

No test project or test files exist in this repository. There is no established test framework or pattern.

## CI/CD -- Bifrost / Cicada

- **Cicada CI/CD** handles all builds (`.cicada-policy.yml`).
- **GitHub Actions is explicitly forbidden** (policy: `github_actions: forbidden`).
- Build command (from `.bifrost/deploy-manifest.json`):
  ```
  dotnet publish --configuration Release --runtime osx-arm64 --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true
  ```
- Additional supported RIDs: `win-x64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`
- Deploy branch: `hee-haw`
- Secret required: `GITHUB_TOKEN` (for GitHub API rate limiting)
- No code signing: `darwin_codesign_skip: true`

## Runtime Identifiers

The supported RuntimeIdentifiers (from `GithubLauncher.csproj`):
```
win-x64;linux-x64;linux-arm64;osx-x64;osx-arm64
```
