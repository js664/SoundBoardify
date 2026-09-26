using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace VRSoundboard;

/// <summary>Small, bounded client for the community MyInstants API.</summary>
public sealed class MarketplaceService
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = TimeSpan.FromSeconds(12) };
    private const string ApiRoot = "https://myinstants-api.vercel.app/";
    private const int MaxCatalogBytes = 2 * 1024 * 1024;
    private static readonly HttpClient BridgeClient = new() { Timeout = TimeSpan.FromSeconds(25) };
    private readonly ConcurrentDictionary<string, (MarketplaceSound Sound, DateTimeOffset Added)> _known = new(StringComparer.Ordinal);
    private Uri? _browserBridge;
    private string? _browserBridgeToken;

    static MarketplaceService() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("SimplySound/1.0 (MyInstants marketplace)");

    public void ConfigureBrowserBridge(int port, string token)
    {
        if (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(token)) return;
        _browserBridge = new Uri($"http://127.0.0.1:{port}/download", UriKind.Absolute);
        _browserBridgeToken = token;
    }

    public async Task<IReadOnlyList<MarketplaceSound>> SearchAsync(string? query, CancellationToken cancellationToken)
    {
        var normalized = (query ?? string.Empty).Trim();
        if (normalized.Length > 80) throw new ArgumentException("Search is limited to 80 characters.");
        var relative = normalized.Length == 0 ? "recent" : "search?q=" + Uri.EscapeDataString(normalized);
        using var response = await Client.GetAsync(new Uri(new Uri(ApiRoot), relative), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("MyInstants could not load sounds right now.", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (body.Length + read > MaxCatalogBytes) throw new InvalidDataException("The sound catalog response was too large.");
            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        using var json = JsonDocument.Parse(body.ToArray());
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The sound catalog returned an unexpected response.");

        var results = new List<MarketplaceSound>(Math.Min(data.GetArrayLength(), 60));
        foreach (var item in data.EnumerateArray())
        {
            if (!ReadString(item, "id", out var id) || id.Length > 120 ||
                !ReadString(item, "title", out var title) || title.Length > 200 ||
                !ReadString(item, "url", out var pageUrl) || !IsAllowedPageUrl(pageUrl) ||
                !ReadString(item, "mp3", out var audioUrl) || !IsAllowedMp3Url(audioUrl)) continue;

            var result = new MarketplaceSound(id, title, pageUrl, audioUrl);
            _known[id] = (result, DateTimeOffset.UtcNow);
            results.Add(result);
            if (results.Count == 60) break;
        }

        PruneCache();
        return results;
    }

    public bool TryGet(string id, out MarketplaceSound sound)
    {
        if (_known.TryGetValue(id, out var cached) && DateTimeOffset.UtcNow - cached.Added < TimeSpan.FromMinutes(30))
        {
            sound = cached.Sound;
            return true;
        }
        sound = default!;
        return false;
    }

    public async Task<byte[]> DownloadAsync(MarketplaceSound sound, long maxBytes, CancellationToken cancellationToken)
    {
        if (!IsAllowedMp3Url(sound.AudioUrl)) throw new InvalidDataException("This sound has an invalid source URL.");
        var cap = (int)Math.Clamp(maxBytes, 1, 25 * 1024 * 1024);
        if (_browserBridge is not null && !string.IsNullOrWhiteSpace(_browserBridgeToken))
            return await DownloadThroughBrowserAsync(sound, cap, cancellationToken);
        using var response = await Client.GetAsync(sound.AudioUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("MyInstants could not download this sound.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > cap)
            throw new InvalidDataException("This sound exceeds your configured upload limit.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(Math.Min(cap, 256 * 1024));
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > cap) throw new InvalidDataException("This sound exceeds your configured upload limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        if (buffer.Length < 4 || !LooksLikeMpegAudio(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)))
            throw new InvalidDataException("The downloaded file is not a supported MP3 sound.");
        return buffer.ToArray();
    }

    private async Task<byte[]> DownloadThroughBrowserAsync(MarketplaceSound sound, int cap, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _browserBridge)
        {
            Content = JsonContent.Create(new { audioUrl = sound.AudioUrl })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _browserBridgeToken);
        using var response = await BridgeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var reason = await response.Content.ReadAsStringAsync(cancellationToken);
            if (reason.Length > 180) reason = reason[..180];
            throw new HttpRequestException(string.IsNullOrWhiteSpace(reason) ? "The browser could not retrieve this sound." : reason, null, response.StatusCode);
        }
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > cap)
            throw new InvalidDataException("This sound exceeds your configured upload limit.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(Math.Min(cap, 256 * 1024));
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > cap) throw new InvalidDataException("This sound exceeds your configured upload limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        if (buffer.Length < 4 || !LooksLikeMpegAudio(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)))
            throw new InvalidDataException("The downloaded file is not a supported MP3 sound.");
        return buffer.ToArray();
    }

    public static bool IsAllowedPageUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
           uri.IsDefaultPort && uri.IdnHost.Equals("www.myinstants.com", StringComparison.OrdinalIgnoreCase) &&
           uri.AbsolutePath.StartsWith("/en/instant/", StringComparison.Ordinal);

    public static bool IsAllowedMp3Url(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
           uri.IsDefaultPort && uri.IdnHost.Equals("www.myinstants.com", StringComparison.OrdinalIgnoreCase) &&
           uri.AbsolutePath.StartsWith("/media/sounds/", StringComparison.Ordinal) &&
           uri.AbsolutePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);

    private static bool ReadString(JsonElement item, string name, out string value)
    {
        value = string.Empty;
        if (!item.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString()?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool LooksLikeMpegAudio(ReadOnlySpan<byte> bytes)
    {
        // Accept ID3-prefixed MP3 and raw MPEG audio frame sync; reject HTML/error pages.
        if (bytes.Length >= 3 && bytes[..3].SequenceEqual("ID3"u8)) return true;
        for (var i = 0; i < Math.Min(bytes.Length - 1, 4096); i++)
            if (bytes[i] == 0xff && (bytes[i + 1] & 0xe0) == 0xe0) return true;
        return false;
    }

    private void PruneCache()
    {
        var expiry = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(30);
        foreach (var entry in _known)
            if (entry.Value.Added < expiry) _known.TryRemove(entry.Key, out _);
        if (_known.Count > 2000)
            foreach (var entry in _known.OrderBy(pair => pair.Value.Added).Take(_known.Count - 1500))
                _known.TryRemove(entry.Key, out _);
    }
}

public sealed record MarketplaceSound(string Id, string Title, string PageUrl, string AudioUrl);
