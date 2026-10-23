# Jellyfin Plugin: Track Memory

[![Build and Test](https://github.com/Soviann/jellyfin-plugin-track-memory/actions/workflows/build.yaml/badge.svg)](https://github.com/Soviann/jellyfin-plugin-track-memory/actions/workflows/build.yaml)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)
[![Target Jellyfin ABI](https://img.shields.io/badge/Jellyfin-10.10.x-purple.svg)](https://github.com/jellyfin/jellyfin)
[![Target Framework](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)

A server-side Jellyfin plugin that automatically remembers and restores your chosen audio and subtitle tracks per title and across TV series episodes, across all playback sessions, devices, and rewatches.

---

## 📌 The Problem

In Jellyfin, managing audio and subtitle track preferences is a frequent source of frustration:

1. **Amnesia Between Sessions & Rewatches:** When you pick an alternate audio track (e.g. Japanese audio, Original Version, Director's Commentary) or specific subtitles (e.g. VOSTFR, Forced, English SDH) for a movie or episode, Jellyfin forgets your choice as soon as playback ends. Re-opening the title days later or rewatching it forces you to manually re-select the tracks every single time.
2. **Client-Side "Previous Item" Limitations:** Jellyfin provides a native setting *"Set audio/subtitle track based on previous item"*. However, this setting:
   - Only works in continuous sequential autoplay within the exact same client session.
   - Fails completely when switching between different series or movies.
   - Resets whenever you close the app, pause for a day, or switch devices.
3. **Smart TV & Native App Incompatibilities:** On Smart TVs (LG webOS, Samsung Tizen, Android TV, Apple TV) where custom browser scripts (Tampermonkey/client mods) cannot run, players constantly reset back to the media container's embedded default track flags or global account defaults.
4. **Media Files with Multiple or Non-Standard Tracks:** Files with commentary tracks, multiple language tracks (e.g. Multi French/English), or multiple subtitle types (Full vs Signs & Songs) repeatedly default to the wrong stream.

---

## 💡 How It Works

`Track Memory` is **100% server-side** and operates transparently without requiring any client-side modifications:

```
[ Jellyfin Client (TV, Web, Mobile) ]
                 │
                 │ 1. POST /Items/{ItemId}/PlaybackInfo
                 ▼
     ┌───────────────────────┐
     │ TrackMemoryMiddleware │ ──► Capture explicit selections (details page / URL params)
     └───────────────────────┘
                 │
                 ▼
     [ Core Jellyfin Engine ] ──► Computes MediaSources & transcode profiles
                 │
                 ▼
     ┌───────────────────────┐
     │ TrackMemoryMiddleware │ ──► Injects remembered DefaultAudioStreamIndex
     └───────────────────────┘     and DefaultSubtitleStreamIndex (decompresses gzip/brotli)
                 │
                 ▼
[ Jellyfin Client (Plays with correct audio & subs automatically) ]
                 │
                 │ 2. In-playback track switch via remote control
                 ▼
    ┌─────────────────────────┐
    │  PlaybackTrackerService │ ◄── Listens to ISessionManager (PlaybackStart/Progress/Stopped)
    └─────────────────────────┘
                 │
                 ▼
      [ TrackMemoryStore ]    ──► Non-blocking concurrent cache with async disk persistence
```

### Key Capabilities

* **TV Series Language Inheritance (v1.1+):**
  When you watch an episode of a TV series and choose Japanese audio with English subtitles, the plugin saves your preference at both the episode and the series level (`SeriesId`). Subsequent episodes automatically match the same audio and subtitle languages (using ISO-639 codes and synonyms), even if the stream indices differ between episode encode files!
* **Item-Level Exact Memory:**
  Movies and individual media remember their specific track indices persistently across sessions and rewatches.
* **In-Playback Track Switch Detection:**
  Changing tracks using your TV remote control or on-screen player dynamically saves the new preference in real time via `ISessionManager.PlaybackProgress` and `PlaybackStopped` events.
* **Pre-Playback Selection Capture:**
  Selecting an audio or subtitle track from the media details screen before hitting "Play" is captured immediately upon launching.
* **Explicit Subtitle Disable Support:**
  Turning subtitles off is recorded explicitly (`SubtitleDisabled = true` / `-1`), preventing Jellyfin from falling back to forced or default subtitles on subsequent playbacks.
* **Zero Client Footprint:**
  Works natively on LG webOS, Samsung Tizen, Android TV, Fire TV, Apple TV, iOS, Android, Jellyfin Web, and Jellyfin Media Player.
* **Transparent Compression Handling:**
  Safely decompresses, inspects, and re-compresses HTTP payloads (Gzip, Brotli, Deflate) commonly used by webOS and modern browser clients.
* **High Performance & Safety:**
  Non-blocking thread-safe `ConcurrentDictionary` store backed by scheduled asynchronous disk writes (`track-memory.json`), ensuring zero playback latency.

---

## 🚀 Installation

### Option 1: Via Jellyfin Plugin Repository (Recommended)

1. Open your **Jellyfin Dashboard** $\rightarrow$ **Plugins** $\rightarrow$ **Repositories** tab.
2. Click the **➕ Add Repository** button.
3. Enter the repository details:
   - **Repository Name:** `Track Memory Repository` (or any custom name)
   - **Repository URL:**
     ```
     https://raw.githubusercontent.com/Soviann/jellyfin-plugin-track-memory/main/manifest.json
     ```
4. Click **Save**.
5. Switch to the **Catalog** tab.
6. Find **Track Memory** under the **Playback** category and click **Install**.
7. Restart your Jellyfin server / container when prompted or at your convenience.

---

### Option 2: Manual Installation (Docker / Host)

1. Download the latest release `TrackMemory_1.1.0.0.zip` from the [Releases page](https://github.com/Soviann/jellyfin-plugin-track-memory/releases).
2. Create a folder in your Jellyfin `plugins` directory:
   ```bash
   mkdir -p /path/to/jellyfin/config/plugins/TrackMemory_1.1.0.0
   ```
3. Extract `Jellyfin.Plugin.TrackMemory.dll` and `meta.json` into this folder:
   ```bash
   unzip TrackMemory_1.1.0.0.zip -d /path/to/jellyfin/config/plugins/TrackMemory_1.1.0.0/
   ```
4. Restart your Jellyfin server / container:
   ```bash
   docker restart jellyfin
   ```

---

## ⚙️ Configuration

Once installed, navigate to **Jellyfin Dashboard** $\rightarrow$ **Plugins** $\rightarrow$ **Track Memory**:

* **Enable Track Memory:** Master toggle for the plugin (default: `Enabled`).
* **Remember Audio Tracks:** Toggle persistence of audio stream selections (default: `Enabled`).
* **Remember Subtitle Tracks:** Toggle persistence of subtitle stream selections (default: `Enabled`).
* **Enable Debug Logging:** Outputs detailed track-matching and stream-index injection logs to your Jellyfin server logs for troubleshooting.

---

## ❓ Frequently Asked Questions (FAQ)

### Does this work on Smart TVs (LG webOS, Samsung Tizen, Android TV)?
**Yes.** Because Track Memory operates purely at the server API level by rewriting the `DefaultAudioStreamIndex` and `DefaultSubtitleStreamIndex` in the `/PlaybackInfo` response, the client receives the stream already configured with your preferred tracks. No client-side modification, root, or jailbreak is needed.

### How is this different from Jellyfin's built-in "Set audio track based on previous item"?
Jellyfin's built-in setting is client-side, ephemeral, and only transfers settings to the immediately following episode during continuous playback. If you pause, close the app, watch another show, or switch devices, the built-in setting resets. Track Memory persists your selections on the server permanently per user, per title, and per series.

### What happens if stream indices differ between episodes in a TV series?
Version 1.1+ records the ISO language code (e.g. `jpn`, `eng`, `fre`) alongside the stream index. When playing the next episode, the plugin matches the streams by language first. If Episode 1 has Japanese audio at index 2, and Episode 2 has Japanese audio at index 1, Track Memory matches the language and selects the correct stream automatically.

### Does this modify my video files (MKV, MP4)?
**No.** Track Memory is 100% non-destructive. It never modifies your media files or container flags. All data is kept in a small JSON database (`track-memory.json`) in the Jellyfin plugin configuration folder.

### Are preferences shared between users?
**No.** Preferences are isolated per Jellyfin user (`UserId`). Each family member or account keeps their own distinct audio and subtitle selections.

---

## 🛠️ Building & Testing

The project targets .NET 8.0 (`net8.0`) matching the Jellyfin 10.10.x ABI.

### Build & Run Tests with Docker (Recommended)
```bash
docker run --rm -v $(pwd):/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet test -c Release
```

### Build with Local .NET SDK
```bash
dotnet build -c Release
dotnet test -c Release
```

### Validate Repository Manifest
```bash
python3 scripts/validate-manifest.py --check-remote
```

---

## 🔍 Search Keywords & Discoverability

*Jellyfin remember audio track, Jellyfin remember subtitles, Jellyfin save subtitle selection between episodes, Jellyfin keep audio language TV series, Jellyfin audio reset bug, Jellyfin webOS subtitle persistence, Jellyfin subtitle selection per show, Jellyfin default audio stream override, Jellyfin TV show language memory, Jellyfin playback track persistence plugin.*

---

## 📄 License

Licensed under the [Apache License, Version 2.0](LICENSE).  
Copyright © 2026 Soviann.
