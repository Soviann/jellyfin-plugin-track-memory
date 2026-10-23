# Series-Level Track Memory and Lifecycle Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix Track Memory plugin lifecycle and POST parsing bugs, and add Series-level track memory inheritance so audio and subtitle language choices carry across episodes automatically.

**Architecture:** 
1. Make `PlaybackTrackerService` implement `Microsoft.Extensions.Hosting.IHostedService` and register it via `AddHostedService` so ASP.NET Core initializes it at server startup.
2. Fix `TrackMemoryMiddleware` JSON body parsing to handle string-encoded integer indices safely (`JsonValueKind.String` and `JsonValueKind.Number`), avoiding `InvalidOperationException`.
3. Enrich `TrackSelection` with `AudioLanguage`, `SubtitleLanguage`, and `SubtitleDisabled` flags.
4. Support Series-level fallback: when saving selections for an episode, also record the preferences under its `SeriesId`. When loading preferences for an episode without a specific record, inherit the series preferences and resolve the matching stream index by language code.

**Tech Stack:** C# .NET 8.0, Jellyfin.Controller 10.10.7, Jellyfin.Model 10.10.7, xUnit, Moq, Docker (.NET SDK 8.0 runner).

**Spec:** Problem statement and root-cause analysis from debugging session on 2026-10-23.

## Global Constraints
- Target Jellyfin ABI: 10.10.x.
- Compatibility: Do not break existing movie or single-item track memory.
- Non-destructive: Fallback safely if stream language is missing or untagged.
- Strict three-case test coverage (nominal, boundary, error) on all new logic.
- Verify with `docker run --rm -v $(pwd):/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet test`.

## Review Focus
- Input: String-encoded stream indices (`"AudioStreamIndex": "2"`) in PlaybackInfo POST body must not crash middleware.
- Input: Episode without individual selection inherits series audio/subtitle language when series has recorded preference.
- Input: Subtitle disabled selection (-1 or explicitly turned off) must propagate correctly without defaulting to first available subtitle.
- Lifecycle: `PlaybackTrackerService` must hook into `ISessionManager` events upon `StartAsync` and detach cleanly on `StopAsync`.
- Fallback: When stream languages do not match between different episode encodes, fallback gracefully to stream indices.

---

### Task 1: Enrich `TrackSelection` DTO and Update `TrackMemoryStore`

**Files:**
- Modify: `src/Jellyfin.Plugin.TrackMemory/Domain/TrackSelection.cs`
- Modify: `src/Jellyfin.Plugin.TrackMemory/Services/ITrackMemoryStore.cs`
- Modify: `src/Jellyfin.Plugin.TrackMemory/Services/TrackMemoryStore.cs`
- Test: `tests/Jellyfin.Plugin.TrackMemory.Tests/Services/TrackMemoryStoreTests.cs`

- [x] **Step 1: Write failing unit tests for extended TrackSelection and Series-aware store**
  Add unit tests in `TrackMemoryStoreTests.cs`:
  - Nominal: Save and retrieve selection with `AudioLanguage`, `SubtitleLanguage`, `SubtitleDisabled`.
  - Boundary: Retrieve item with no direct selection but with a parent `seriesId` present $\rightarrow$ returns series selection.
  - Boundary: Subtitle disabled flag (`SubtitleDisabled == true`) persists through serialization and disk reload.
- [x] **Step 2: Run tests to verify failure**
  Run: `docker run --rm -v /Users/nicolasvasse/Soviann/jellyfin-plugin-track-memory:/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet test`
- [x] **Step 3: Update `TrackSelection` DTO**
  Add `string? AudioLanguage`, `string? SubtitleLanguage`, and `bool SubtitleDisabled` properties to `TrackSelection`. Update constructors and `StoredRecord` JSON mapping.
- [x] **Step 4: Update `ITrackMemoryStore` and `TrackMemoryStore`**
  - Add overload `TrackSelection? GetSelection(Guid userId, Guid itemId, Guid? seriesId = null)`.
  - Add overload `void SaveSelection(Guid userId, Guid itemId, Guid? seriesId, int? audioStreamIndex, int? subtitleStreamIndex, string? audioLanguage = null, string? subtitleLanguage = null, bool subtitleDisabled = false)`.
  - When `seriesId` is provided, save or update the series-level key `(userId, seriesId.Value)` in addition to the item-level key.
  - Implement JSON serialization/deserialization for the new fields.
- [x] **Step 5: Run tests to verify pass**
  Verify all unit tests pass green.

---

### Task 2: Fix JSON Body Parsing in `TrackMemoryMiddleware` and Add Series Language Resolution

**Files:**
- Modify: `src/Jellyfin.Plugin.TrackMemory/Api/TrackMemoryMiddleware.cs`
- Test: `tests/Jellyfin.Plugin.TrackMemory.Tests/Api/TrackMemoryMiddlewareTests.cs`

- [x] **Step 1: Write failing unit tests for string-encoded POST indices and language matching**
  Add tests in `TrackMemoryMiddlewareTests.cs`:
  - Error/Boundary: POST body containing string indices `{"AudioStreamIndex": "2", "SubtitleStreamIndex": "3"}` does not throw and extracts indices correctly.
  - Nominal: Response rewriting matches audio and subtitle by `AudioLanguage` and `SubtitleLanguage` in `MediaStreams` when series selection is used.
  - Boundary: Subtitle disabled (`SubtitleDisabled = true` or index -1) forces `DefaultSubtitleStreamIndex = -1`.
- [x] **Step 2: Run tests to verify failure**
  Run: `docker run --rm -v /Users/nicolasvasse/Soviann/jellyfin-plugin-track-memory:/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet test`
- [x] **Step 3: Fix `InspectRequestForOverridesAsync` in `TrackMemoryMiddleware`**
  Safely handle `JsonValueKind.Number` and `JsonValueKind.String` (via `int.TryParse(element.GetString(), ...)`) for both `AudioStreamIndex` and `SubtitleStreamIndex`.
- [x] **Step 4: Update `TrackMemoryMiddleware` constructor & response rewriting**
  - Inject `ILibraryManager` to resolve `seriesId` for episodes (`item is Episode ep && ep.SeriesId != Guid.Empty`).
  - In `RewritePlaybackInfoResponseAsync`: if `remembered.AudioLanguage` or `remembered.SubtitleLanguage` is specified, scan `MediaStreams` for matching language and set `DefaultAudioStreamIndex` / `DefaultSubtitleStreamIndex` accordingly.
- [x] **Step 5: Run tests to verify pass**
  Verify all middleware tests pass green.

---

### Task 3: Fix `PlaybackTrackerService` Lifecycle via `IHostedService` and Series Tracking

**Files:**
- Modify: `src/Jellyfin.Plugin.TrackMemory/Services/PlaybackTrackerService.cs`
- Modify: `src/Jellyfin.Plugin.TrackMemory/PluginServiceRegistrator.cs`
- Test: `tests/Jellyfin.Plugin.TrackMemory.Tests/Services/PlaybackTrackerServiceTests.cs`

- [x] **Step 1: Write unit tests for `PlaybackTrackerService`**
  Create `PlaybackTrackerServiceTests.cs` testing:
  - Nominal: `StartAsync` subscribes to events, `PlaybackStart` with an episode extracts `seriesId` and stream languages, saving to store.
  - Boundary: `StopAsync` cleanly detaches from session events.
  - Boundary: Subtitle index -1 correctly marks `subtitleDisabled = true`.
- [x] **Step 2: Implement `IHostedService` on `PlaybackTrackerService`**
  - Implement `StartAsync(CancellationToken)` and `StopAsync(CancellationToken)`.
  - In `ProcessPlaybackEvent`, extract language tags from `e.Item.GetMediaStreams()` and `seriesId` if item is `Episode`.
- [x] **Step 3: Update `PluginServiceRegistrator.cs`**
  Register `PlaybackTrackerService` both as singleton and as hosted service:
  ```csharp
  serviceCollection.AddSingleton<PlaybackTrackerService>();
  serviceCollection.AddHostedService(sp => sp.GetRequiredService<PlaybackTrackerService>());
  ```
- [x] **Step 4: Run full test suite**
  Verify 100% green on all unit tests.

---

### Task 4: Build, Package, Deploy to NAS & Verification

**Files:**
- Build release zip in `bin/package`
- Update `manifest.json` version to `1.1.0.0`
- Deploy to Synology NAS `/volume1/docker/jellyfin/config/plugins/`

- [x] **Step 1: Update plugin version to `1.1.0.0`**
  Update `Directory.Build.props`, `manifest.json`, and `meta.json`.
- [x] **Step 2: Build release package via Docker**
  Run `dotnet publish` in docker to produce `Jellyfin.Plugin.TrackMemory.dll` and generate release zip with md5 checksum.
- [x] **Step 3: Deploy to NAS and restart Jellyfin**
  - Copy new plugin dll to NAS `/volume1/docker/jellyfin/config/plugins/Track Memory_1.1.0.0/`.
  - Remove previous version folder `Track Memory_1.0.0.0`.
  - Restart Jellyfin container via `docker compose -p jellyfish restart`.
- [x] **Step 4: Verify in Jellyfin logs on NAS**
  Assert `PlaybackTrackerService` starts, no `InvalidOperationException` occurs on PlaybackInfo requests, and `track-memory.json` is generated upon stream selection.
