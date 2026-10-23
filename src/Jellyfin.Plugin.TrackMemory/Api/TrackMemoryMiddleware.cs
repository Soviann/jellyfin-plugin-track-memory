using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.TrackMemory.Domain;
using Jellyfin.Plugin.TrackMemory.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TrackMemory.Api;

/// <summary>
/// ASP.NET Core Middleware that intercepts /Items/{Id}/PlaybackInfo calls.
/// On Request: captures explicit track overrides from details page or player launch.
/// On Response: rewrites DefaultAudioStreamIndex and DefaultSubtitleStreamIndex with remembered values if no explicit request override was present.
/// </summary>
public class TrackMemoryMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TrackMemoryMiddleware> _logger;
    private readonly ITrackMemoryStore _store;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackMemoryMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in pipeline.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="store">The track memory store.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    public TrackMemoryMiddleware(
        RequestDelegate next,
        ILogger<TrackMemoryMiddleware> logger,
        ITrackMemoryStore store,
        IUserManager userManager,
        ILibraryManager libraryManager)
    {
        _next = next;
        _logger = logger;
        _store = store;
        _userManager = userManager;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Invokes the middleware.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A task representing asynchronous execution.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        var config = Plugin.Instance?.Configuration;
        if (config != null && !config.EnablePlugin)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        if (!IsPlaybackInfoRequest(path, out var itemId))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var userId = ExtractUserId(context);

        var item = _libraryManager.GetItemById(itemId);
        Guid? seriesId = null;
        if (item is Episode episode && episode.SeriesId != Guid.Empty)
        {
            seriesId = episode.SeriesId;
        }

        // 1. Inspect request for explicit track selections (e.g. from item details page dropdown)
        var explicitRequestIndices = await InspectRequestForOverridesAsync(context, userId, itemId).ConfigureAwait(false);

        // If client explicitly requested audio or subtitle stream index in the request,
        // it takes priority and will be respected by downstream Jellyfin, and we record it now.
        if (userId != Guid.Empty && (explicitRequestIndices.AudioIndex.HasValue || explicitRequestIndices.SubtitleIndex.HasValue))
        {
            string? audioLang = null;
            string? subLang = null;
            bool subDisabled = explicitRequestIndices.SubtitleIndex == -1;

            if (item != null)
            {
                var streams = item.GetMediaStreams();
                if (explicitRequestIndices.AudioIndex.HasValue)
                {
                    audioLang = streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.Index == explicitRequestIndices.AudioIndex.Value)?.Language;
                }

                if (explicitRequestIndices.SubtitleIndex.HasValue && explicitRequestIndices.SubtitleIndex.Value >= 0)
                {
                    subLang = streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == explicitRequestIndices.SubtitleIndex.Value)?.Language;
                }
            }

            _logger.LogDebug(
                "Captured explicit track selection from request for user {UserId}, item {ItemId} (series {SeriesId}): Audio={Audio} ({AudioLang}), Sub={Sub} ({SubLang})",
                userId,
                itemId,
                seriesId,
                explicitRequestIndices.AudioIndex,
                audioLang,
                explicitRequestIndices.SubtitleIndex,
                subLang);

            _store.SaveSelection(
                userId,
                itemId,
                seriesId,
                explicitRequestIndices.AudioIndex,
                explicitRequestIndices.SubtitleIndex,
                audioLang,
                subLang,
                subDisabled);
        }

        // 2. Intercept response to rewrite DefaultAudioStreamIndex / DefaultSubtitleStreamIndex
        var originalBodyStream = context.Response.Body;
        await using var responseBodyStream = new MemoryStream();
        context.Response.Body = responseBodyStream;

        try
        {
            await _next(context).ConfigureAwait(false);

            if (context.Response.StatusCode == StatusCodes.Status200OK
                && (context.Response.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                responseBodyStream.Seek(0, SeekOrigin.Begin);
                var contentEncoding = context.Response.Headers.ContentEncoding.ToString();

                Stream decompressedStream = responseBodyStream;
                bool isCompressed = false;

                if (string.Equals(contentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
                {
                    decompressedStream = new GZipStream(responseBodyStream, CompressionMode.Decompress, leaveOpen: true);
                    isCompressed = true;
                }
                else if (string.Equals(contentEncoding, "br", StringComparison.OrdinalIgnoreCase))
                {
                    decompressedStream = new BrotliStream(responseBodyStream, CompressionMode.Decompress, leaveOpen: true);
                    isCompressed = true;
                }
                else if (string.Equals(contentEncoding, "deflate", StringComparison.OrdinalIgnoreCase))
                {
                    decompressedStream = new DeflateStream(responseBodyStream, CompressionMode.Decompress, leaveOpen: true);
                    isCompressed = true;
                }

                byte[] modifiedJsonBytes;
                try
                {
                    var remembered = userId != Guid.Empty ? _store.GetSelection(userId, itemId, seriesId) : null;
                    modifiedJsonBytes = await RewritePlaybackInfoResponseAsync(
                        decompressedStream,
                        remembered,
                        explicitRequestIndices,
                        config).ConfigureAwait(false);
                }
                finally
                {
                    if (isCompressed)
                    {
                        await decompressedStream.DisposeAsync().ConfigureAwait(false);
                    }
                }

                context.Response.Body = originalBodyStream;

                if (isCompressed)
                {
                    using var compressedMs = new MemoryStream();
                    if (string.Equals(contentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
                    {
                        await using (var gz = new GZipStream(compressedMs, CompressionLevel.Fastest, leaveOpen: true))
                        {
                            await gz.WriteAsync(modifiedJsonBytes).ConfigureAwait(false);
                        }
                    }
                    else if (string.Equals(contentEncoding, "br", StringComparison.OrdinalIgnoreCase))
                    {
                        await using (var br = new BrotliStream(compressedMs, CompressionLevel.Fastest, leaveOpen: true))
                        {
                            await br.WriteAsync(modifiedJsonBytes).ConfigureAwait(false);
                        }
                    }
                    else if (string.Equals(contentEncoding, "deflate", StringComparison.OrdinalIgnoreCase))
                    {
                        await using (var def = new DeflateStream(compressedMs, CompressionLevel.Fastest, leaveOpen: true))
                        {
                            await def.WriteAsync(modifiedJsonBytes).ConfigureAwait(false);
                        }
                    }

                    var finalCompressedBytes = compressedMs.ToArray();
                    context.Response.ContentLength = finalCompressedBytes.Length;
                    await originalBodyStream.WriteAsync(finalCompressedBytes).ConfigureAwait(false);
                }
                else
                {
                    context.Response.ContentLength = modifiedJsonBytes.Length;
                    await originalBodyStream.WriteAsync(modifiedJsonBytes).ConfigureAwait(false);
                }
            }
            else
            {
                responseBodyStream.Seek(0, SeekOrigin.Begin);
                context.Response.Body = originalBodyStream;
                await responseBodyStream.CopyToAsync(originalBodyStream).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while processing PlaybackInfo in TrackMemoryMiddleware for item {ItemId}", itemId);
            responseBodyStream.Seek(0, SeekOrigin.Begin);
            context.Response.Body = originalBodyStream;
            await responseBodyStream.CopyToAsync(originalBodyStream).ConfigureAwait(false);
        }
    }

    private static bool IsPlaybackInfoRequest(string path, out Guid itemId)
    {
        itemId = Guid.Empty;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // Expected format: /Items/{itemId}/PlaybackInfo
        const string itemsPrefix = "/Items/";
        const string suffix = "/PlaybackInfo";

        var idxPrefix = path.IndexOf(itemsPrefix, StringComparison.OrdinalIgnoreCase);
        if (idxPrefix >= 0 && path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            var start = idxPrefix + itemsPrefix.Length;
            var end = path.Length - suffix.Length;
            if (end > start)
            {
                var idStr = path.Substring(start, end - start);
                return Guid.TryParse(idStr, out itemId);
            }
        }

        return false;
    }

    private Guid ExtractUserId(HttpContext context)
    {
        // 1. Try Query string ?userId=...
        var userIdQuery = context.Request.Query["userId"].ToString();
        if (Guid.TryParse(userIdQuery, out var userGuid))
        {
            return userGuid;
        }

        // 2. Try ClaimsPrincipal
        var user = context.User;
        if (user != null)
        {
            var claim = user.FindFirst("Jellyfin-UserId") ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
            if (claim != null && Guid.TryParse(claim.Value, out var claimGuid))
            {
                return claimGuid;
            }
        }

        return Guid.Empty;
    }

    private async Task<(int? AudioIndex, int? SubtitleIndex)> InspectRequestForOverridesAsync(
        HttpContext context,
        Guid userId,
        Guid itemId)
    {
        int? audioIndex = null;
        int? subtitleIndex = null;

        // Query string inspection (?audioStreamIndex=... & subtitleStreamIndex=...)
        if (context.Request.Query.TryGetValue("audioStreamIndex", out var audioVal) && int.TryParse(audioVal, out var aIdx))
        {
            audioIndex = aIdx;
        }

        if (context.Request.Query.TryGetValue("subtitleStreamIndex", out var subVal) && int.TryParse(subVal, out var sIdx))
        {
            subtitleIndex = sIdx;
        }

        // POST body inspection if body is JSON
        if (HttpMethods.IsPost(context.Request.Method)
            && (context.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) ?? false)
            && context.Request.ContentLength > 0)
        {
            context.Request.EnableBuffering();
            try
            {
                using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                context.Request.Body.Position = 0;

                var (bodyAudio, bodySub) = ParseJsonStreamIndices(body);
                if (bodyAudio.HasValue)
                {
                    audioIndex = bodyAudio;
                }
                if (bodySub.HasValue)
                {
                    subtitleIndex = bodySub;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse PlaybackInfo POST request body");
                context.Request.Body.Position = 0;
            }
        }

        return (audioIndex, subtitleIndex);
    }

    /// <summary>
    /// Safely parses AudioStreamIndex and SubtitleStreamIndex from a JSON string, accepting either numbers or strings.
    /// </summary>
    /// <param name="json">The JSON payload.</param>
    /// <returns>A tuple with audio and subtitle stream indices if present.</returns>
    public static (int? AudioIndex, int? SubtitleIndex) ParseJsonStreamIndices(string json)
    {
        int? audioIndex = null;
        int? subtitleIndex = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("AudioStreamIndex", out var jsonAudio))
            {
                if (jsonAudio.ValueKind == JsonValueKind.Number && jsonAudio.TryGetInt32(out var jsonAIdx))
                {
                    audioIndex = jsonAIdx;
                }
                else if (jsonAudio.ValueKind == JsonValueKind.String && int.TryParse(jsonAudio.GetString(), out var strAIdx))
                {
                    audioIndex = strAIdx;
                }
            }

            if (doc.RootElement.TryGetProperty("SubtitleStreamIndex", out var jsonSub))
            {
                if (jsonSub.ValueKind == JsonValueKind.Number && jsonSub.TryGetInt32(out var jsonSIdx))
                {
                    subtitleIndex = jsonSIdx;
                }
                else if (jsonSub.ValueKind == JsonValueKind.String && int.TryParse(jsonSub.GetString(), out var strSIdx))
                {
                    subtitleIndex = strSIdx;
                }
            }
        }
        catch
        {
            // Ignore parse errors, return what was found or null
        }

        return (audioIndex, subtitleIndex);
    }

    /// <summary>
    /// Rewrites the PlaybackInfoResponse JSON stream to inject remembered track selections.
    /// </summary>
    public static async Task<byte[]> RewritePlaybackInfoResponseAsync(
        Stream responseStream,
        TrackSelection? remembered,
        (int? AudioIndex, int? SubtitleIndex) explicitRequest,
        Configuration.PluginConfiguration? config)
    {
        JsonNode? jsonNode;
        try
        {
            jsonNode = await JsonNode.ParseAsync(responseStream).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            responseStream.Seek(0, SeekOrigin.Begin);
            using var ms = new MemoryStream();
            await responseStream.CopyToAsync(ms).ConfigureAwait(false);
            return ms.ToArray();
        }

        if (jsonNode is not JsonObject rootObj)
        {
            responseStream.Seek(0, SeekOrigin.Begin);
            using var ms = new MemoryStream();
            await responseStream.CopyToAsync(ms).ConfigureAwait(false);
            return ms.ToArray();
        }

        if (remembered == null || (config != null && !config.EnablePlugin))
        {
            return JsonSerializer.SerializeToUtf8Bytes(rootObj, new JsonSerializerOptions { WriteIndented = false });
        }

        var enableAudio = config?.EnableAudioMemory ?? true;
        var enableSubtitle = config?.EnableSubtitleMemory ?? true;

        if (rootObj.TryGetPropertyValue("MediaSources", out var mediaSourcesNode) && mediaSourcesNode is JsonArray mediaSourcesArray)
        {
            foreach (var mediaSource in mediaSourcesArray)
            {
                if (mediaSource is not JsonObject sourceObj)
                {
                    continue;
                }

                JsonArray? streamsArray = null;
                if (sourceObj.TryGetPropertyValue("MediaStreams", out var msNode) && msNode is JsonArray arr)
                {
                    streamsArray = arr;
                }

                // Rewrite Audio if request did NOT explicitly specify one
                if (enableAudio && !explicitRequest.AudioIndex.HasValue)
                {
                    int? resolvedAudio = null;
                    if (!string.IsNullOrEmpty(remembered.AudioLanguage) && streamsArray != null)
                    {
                        resolvedAudio = FindStreamIndexByLanguage(streamsArray, "Audio", remembered.AudioLanguage);
                    }

                    resolvedAudio ??= remembered.AudioStreamIndex;

                    if (resolvedAudio.HasValue)
                    {
                        sourceObj["DefaultAudioStreamIndex"] = resolvedAudio.Value;
                    }
                }

                // Rewrite Subtitle if request did NOT explicitly specify one
                if (enableSubtitle && !explicitRequest.SubtitleIndex.HasValue)
                {
                    if (remembered.SubtitleDisabled || remembered.SubtitleStreamIndex == -1)
                    {
                        sourceObj["DefaultSubtitleStreamIndex"] = -1;
                    }
                    else
                    {
                        int? resolvedSub = null;
                        if (!string.IsNullOrEmpty(remembered.SubtitleLanguage) && streamsArray != null)
                        {
                            resolvedSub = FindStreamIndexByLanguage(streamsArray, "Subtitle", remembered.SubtitleLanguage);
                        }

                        resolvedSub ??= remembered.SubtitleStreamIndex;

                        if (resolvedSub.HasValue)
                        {
                            sourceObj["DefaultSubtitleStreamIndex"] = resolvedSub.Value;
                        }
                    }
                }
            }
        }

        return JsonSerializer.SerializeToUtf8Bytes(rootObj, new JsonSerializerOptions { WriteIndented = false });
    }

    private static int? FindStreamIndexByLanguage(JsonArray streams, string streamType, string languageCode)
    {
        foreach (var s in streams)
        {
            if (s is not JsonObject sObj)
            {
                continue;
            }

            var type = sObj["Type"]?.GetValue<string>();
            if (!string.Equals(type, streamType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lang = sObj["Language"]?.GetValue<string>();
            if (string.IsNullOrEmpty(lang))
            {
                continue;
            }

            if (AreLanguagesEquivalent(lang, languageCode))
            {
                if (sObj["Index"]?.GetValue<int>() is int idx)
                {
                    return idx;
                }
            }
        }

        return null;
    }

    private static bool AreLanguagesEquivalent(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if ((a.Length >= 2 && b.Length >= 2 && a.StartsWith(b, StringComparison.OrdinalIgnoreCase))
            || (b.Length >= 2 && a.Length >= 2 && b.StartsWith(a, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Common bibliographic vs terminological or non-prefix ISO-639 equivalents
        ReadOnlySpan<(string, string)> synonyms =
        [
            ("fre", "fra"),
            ("ger", "deu"),
            ("jpn", "ja"),
            ("spa", "es"),
            ("chi", "zho"),
            ("dut", "nld"),
            ("cze", "ces"),
            ("gre", "ell")
        ];

        foreach (var (synA, synB) in synonyms)
        {
            if ((string.Equals(a, synA, StringComparison.OrdinalIgnoreCase) && string.Equals(b, synB, StringComparison.OrdinalIgnoreCase))
                || (string.Equals(a, synB, StringComparison.OrdinalIgnoreCase) && string.Equals(b, synA, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
