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
        if (!TryMatchInterceptedRequest(path, out var itemId, out var routeUserId, out var isPlaybackInfo))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var userId = ExtractUserId(context, routeUserId);

        var item = _libraryManager.GetItemById(itemId);
        Guid? seriesId = null;
        if (item is Episode episode && episode.SeriesId != Guid.Empty)
        {
            seriesId = episode.SeriesId;
        }

        // Only explicit query string parameters are treated as immediate overrides
        int? explicitAudio = null;
        int? explicitSub = null;
        if (context.Request.Query.TryGetValue("audioStreamIndex", out var aVal) && int.TryParse(aVal, out var aIdx))
        {
            explicitAudio = aIdx;
        }
        if (context.Request.Query.TryGetValue("subtitleStreamIndex", out var sVal) && int.TryParse(sVal, out var sIdx))
        {
            explicitSub = sIdx;
        }
        var explicitRequestIndices = (explicitAudio, explicitSub);

        // Intercept response to rewrite DefaultAudioStreamIndex / DefaultSubtitleStreamIndex
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

    /// <summary>
    /// Checks whether the request path matches an item details or playback info endpoint.
    /// Supported formats:
    /// - /Users/{userId}/Items/{itemId} (Item Details page on Web, LG webOS, mobile)
    /// - /Items/{itemId}/PlaybackInfo (Playback negotiation)
    /// - /Items/{itemId} (Item Details fallback)
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <param name="itemId">The extracted item identifier.</param>
    /// <param name="routeUserId">The extracted user identifier from the route, if present.</param>
    /// <param name="isPlaybackInfo">True if this is a PlaybackInfo request, false for Item Details.</param>
    /// <returns>True if the request should be intercepted.</returns>
    public static bool TryMatchInterceptedRequest(
        string path,
        out Guid itemId,
        out Guid? routeUserId,
        out bool isPlaybackInfo)
    {
        itemId = Guid.Empty;
        routeUserId = null;
        isPlaybackInfo = false;

        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // 1. /Users/{userId}/Items/{itemId}
        const string usersPrefix = "/Users/";
        const string itemsSub = "/Items/";
        var uIdx = path.IndexOf(usersPrefix, StringComparison.OrdinalIgnoreCase);
        if (uIdx >= 0)
        {
            var afterUsers = uIdx + usersPrefix.Length;
            var iIdx = path.IndexOf(itemsSub, afterUsers, StringComparison.OrdinalIgnoreCase);
            if (iIdx > afterUsers)
            {
                var userStr = path.Substring(afterUsers, iIdx - afterUsers);
                var afterItems = iIdx + itemsSub.Length;
                var nextSlash = path.IndexOf('/', afterItems);
                var itemStr = nextSlash >= 0 ? path.Substring(afterItems, nextSlash - afterItems) : path.Substring(afterItems);

                if (nextSlash < 0 || nextSlash == path.Length - 1)
                {
                    if (Guid.TryParse(userStr, out var uGuid) && Guid.TryParse(itemStr, out itemId))
                    {
                        routeUserId = uGuid;
                        isPlaybackInfo = false;
                        return true;
                    }
                }
            }
        }

        // 2. /Items/{itemId}/PlaybackInfo
        const string itemsPrefix = "/Items/";
        const string playbackSuffix = "/PlaybackInfo";
        var itemsIdx = path.IndexOf(itemsPrefix, StringComparison.OrdinalIgnoreCase);
        if (itemsIdx >= 0 && path.EndsWith(playbackSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var start = itemsIdx + itemsPrefix.Length;
            var end = path.Length - playbackSuffix.Length;
            if (end > start)
            {
                var idStr = path.Substring(start, end - start);
                if (Guid.TryParse(idStr, out itemId))
                {
                    isPlaybackInfo = true;
                    return true;
                }
            }
        }

        // 3. /Items/{itemId} (exact single segment)
        if (itemsIdx >= 0 && !path.EndsWith(playbackSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var start = itemsIdx + itemsPrefix.Length;
            var nextSlash = path.IndexOf('/', start);
            if (nextSlash < 0 || nextSlash == path.Length - 1)
            {
                var idStr = nextSlash >= 0 ? path.Substring(start, nextSlash - start) : path.Substring(start);
                if (Guid.TryParse(idStr, out itemId))
                {
                    isPlaybackInfo = false;
                    return true;
                }
            }
        }

        return false;
    }

    private Guid ExtractUserId(HttpContext context, Guid? routeUserId = null)
    {
        if (routeUserId.HasValue && routeUserId.Value != Guid.Empty)
        {
            return routeUserId.Value;
        }

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
                            resolvedSub = FindStreamIndexByLanguage(
                                streamsArray,
                                "Subtitle",
                                remembered.SubtitleLanguage,
                                remembered.SubtitleIsHearingImpaired,
                                remembered.SubtitleIsForced);
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

    private static int? FindStreamIndexByLanguage(
        JsonArray streams,
        string streamType,
        string languageCode,
        bool? isHearingImpaired = null,
        bool? isForced = null)
    {
        int? firstLangMatch = null;

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

            if (!AreLanguagesEquivalent(lang, languageCode))
            {
                continue;
            }

            var idxNode = sObj["Index"];
            if (idxNode == null)
            {
                continue;
            }

            int idx;
            try
            {
                idx = idxNode.GetValue<int>();
            }
            catch
            {
                continue;
            }

            firstLangMatch ??= idx;

            if (string.Equals(streamType, "Subtitle", StringComparison.OrdinalIgnoreCase))
            {
                bool streamHearingImpaired = false;
                if (sObj.TryGetPropertyValue("IsHearingImpaired", out var hiNode) && hiNode != null)
                {
                    try { streamHearingImpaired = hiNode.GetValue<bool>(); } catch { }
                }

                if (!streamHearingImpaired)
                {
                    var title = sObj["Title"]?.GetValue<string>() ?? string.Empty;
                    if (title.Contains("SDH", StringComparison.OrdinalIgnoreCase) || title.Contains("CC", StringComparison.OrdinalIgnoreCase))
                    {
                        streamHearingImpaired = true;
                    }
                }

                bool streamForced = false;
                if (sObj.TryGetPropertyValue("IsForced", out var fNode) && fNode != null)
                {
                    try { streamForced = fNode.GetValue<bool>(); } catch { }
                }

                // Check for exact variant match:
                if (isHearingImpaired == true && streamHearingImpaired)
                {
                    return idx;
                }

                if (isForced == true && streamForced)
                {
                    return idx;
                }

                if (isHearingImpaired == false && isForced == false && !streamHearingImpaired && !streamForced)
                {
                    return idx;
                }
            }
            else
            {
                return idx;
            }
        }

        return firstLangMatch;
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
