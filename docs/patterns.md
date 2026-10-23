# Jellyfin Plugin Track Memory — Codebase Map

## Architecture Overview
- Target ABI: Jellyfin `10.10.x` (.NET 8.0)
- Purpose: Server-side audio & subtitle stream memory per title & series across playback sessions.

## File Map
- `src/Jellyfin.Plugin.TrackMemory/`
  - `Plugin.cs`: Base plugin entry point, plugin GUID & web configuration page registration.
  - `PluginServiceRegistrator.cs`: Registers DI services (`ITrackMemoryStore`, `PlaybackTrackerService`, `IStartupFilter`).
  - `Domain/TrackSelection.cs`: DTO representing remembered stream indices and language codes.
  - `Services/ITrackMemoryStore.cs`: In-memory thread-safe cache interface with async JSON file persistence.
  - `Services/TrackMemoryStore.cs`: Thread-safe `ConcurrentDictionary` store backed by `track-memory.json` flush timer.
  - `Services/PlaybackTrackerService.cs`: Event listener hooking into `ISessionManager.PlaybackStart/Progress/Stopped`.
  - `Api/StartupFilter.cs`: ASP.NET Core `IStartupFilter` inserting `TrackMemoryMiddleware`.
  - `Api/TrackMemoryMiddleware.cs`: Intercepts `/Items/{Id}/PlaybackInfo` (request override extraction & response rewriting).
  - `Configuration/PluginConfiguration.cs`: User configuration options (enable plugin, audio, subtitles, debug).
- `tests/Jellyfin.Plugin.TrackMemory.Tests/`
  - `Services/TrackMemoryStoreTests.cs`: Unit tests for store caching and persistence.
  - `Api/TrackMemoryMiddlewareTests.cs`: Unit tests for PlaybackInfo response rewriting.

## Gotchas & Rules
- **Hosted Services**: Any background event listener must implement `Microsoft.Extensions.Hosting.IHostedService` and be registered via `AddHostedService<T>()` so ASP.NET Core initializes it on startup.
- **System.Text.Json Elements**: `JsonElement.TryGetInt32` throws `InvalidOperationException` if `ValueKind == JsonValueKind.String`; parse via `int.TryParse(element.GetString())` for string variants.
- **Series Scope**: Series items share track selections via `Episode.SeriesId` fallback matching on language codes (`AudioLanguage`, `SubtitleLanguage`).
- **Tests Execution**: Build & test locally via Docker: `docker run --rm -v $(pwd):/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet test`.
