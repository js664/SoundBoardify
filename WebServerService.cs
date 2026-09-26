using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Net.Sockets;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Serilog;
using Microsoft.AspNetCore.Http.Features;

namespace VRSoundboard;

public sealed class WebSocketHub
{
    private sealed record ClientConnection(WebSocket Socket, SemaphoreSlim WriteGate);
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();
    public int ClientCount => _clients.Count;
    public event Action? ClientCountChanged;
    public async Task Accept(HttpContext context)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid();
        _clients[id] = new ClientConnection(socket, new SemaphoreSlim(1, 1));
        ClientCountChanged?.Invoke(); Log.Information("WebSocket connected {Client}", id);
        try
        {
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open)
            {
                var msg = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (msg.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally { _clients.TryRemove(id, out _); ClientCountChanged?.Invoke(); Log.Information("WebSocket disconnected {Client}", id); }
    }
    public async Task Broadcast(string type, object? data)
    {
        if (_clients.IsEmpty) return;
        var clients = _clients.ToArray();
        if (clients.Length == 0) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, data }, JsonOptions));
        var sends = clients.Select(entry => SendToClient(entry.Value, type, bytes));
        await Task.WhenAll(sends);
    }
    private static async Task SendToClient(ClientConnection client, string type, byte[] bytes)
    {
        var acquired = false;
        try
        {
            if (type == "position")
            {
                // Playback position is transient. Skip stale frames when a slow client
                // still has a previous frame in flight; the next tick carries newer state.
                acquired = client.WriteGate.Wait(0);
                if (!acquired) return;
            }
            else
            {
                acquired = await client.WriteGate.WaitAsync(TimeSpan.FromSeconds(1));
                if (!acquired) { client.Socket.Abort(); return; }
            }
            if (client.Socket.State != WebSocketState.Open) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
        }
        catch (OperationCanceledException) { client.Socket.Abort(); }
        catch (WebSocketException) { client.Socket.Abort(); }
        catch (ObjectDisposedException) { client.Socket.Abort(); }
        catch (InvalidOperationException) { client.Socket.Abort(); }
        finally { if (acquired) client.WriteGate.Release(); }
    }
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed class WebServerService(AppCoordinator coordinator, WebSocketHub hub, SteamMicDiagnostic diagnostic, MarketplaceService marketplace)
{
    private const long MaximumUploadBytes = 200L * 1024 * 1024;
    private const long ImageUploadBytes = 5L * 1024 * 1024;
    private const long MultipartOverheadBytes = 1024 * 1024;
    private WebApplication? _app;
    public bool Running => _app is not null;
    public string? LastError { get; private set; }
    public int ClientCount => hub.ClientCount;
    public event Action<int>? StartedOnPort;
    public async Task StartAsync()
    {
        if (_app is not null || !coordinator.Settings.WebEnabled) return;
        var preferred = coordinator.Settings.Port;
        try { await StartAtPortAsync(preferred); }
        catch (Exception ex) when (IsAddressInUse(ex))
        {
            var fallback = FallbackPort(preferred);
            Log.Warning(ex, "Preferred web port {Port} is unavailable; trying fallback port {FallbackPort}", preferred, fallback);
            await StartAtPortAsync(fallback);
        }
    }
    private async Task StartAtPortAsync(int port)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory, WebRootPath = Path.Combine(AppContext.BaseDirectory, "Web") });
        builder.WebHost.UseKestrel(o => { o.ListenAnyIP(port); o.Limits.MaxRequestBodySize = MaximumUploadBytes + MultipartOverheadBytes; });
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaximumUploadBytes);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var requestSize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (requestSize is { IsReadOnly: false })
                requestSize.MaxRequestBodySize = GetRequestBodyLimit(context.Request.Method, context.Request.Path.Value ?? string.Empty, coordinator.Settings.MaxUploadBytes);
            // The service listens on all interfaces, so reject DNS-rebinding hosts and
            // cross-origin browser requests before they can reach the control API.
            if (!IsRequestHostAllowed(context.Request.Host.Host, context.Connection.LocalIpAddress) ||
                !IsSameOrigin(context.Request.Headers["Origin"], context.Request.Scheme, context.Request.Host.Value ?? string.Empty))
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            var ip = AppCoordinator.NormalizeNetworkAddress(context.Connection.RemoteIpAddress);
            var settings = coordinator.Settings;
            if (!AppCoordinator.CanAccessFromNetwork(ip, settings.LanAccess, settings.TailscaleAccess)) { context.Response.StatusCode = 403; return; }
            var token = coordinator.Settings.PairingToken;
            if ((context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/ws")) && !IsPairingAuthorized(ip, token, context.Request.Headers["X-Pairing-Token"], context.Request.Query["token"])) { context.Response.StatusCode = 401; return; }
            await next();
        });
        app.UseWebSockets(); app.UseDefaultFiles(); app.UseStaticFiles();
        app.MapGet("/api/status", () => Results.Ok(new { server = "running", activePort = coordinator.ActiveWebPort, audio = coordinator.Audio.Status, endpoint = coordinator.Audio.EndpointName, monitorEnabled = coordinator.Settings.MonitorLocally, monitorEndpoint = coordinator.Settings.MonitorLocally ? coordinator.Devices.DeviceName(coordinator.Settings.MonitorEndpointId) : null, playback = coordinator.Audio.Playback, clients = hub.ClientCount, url = coordinator.PhoneUrl, wifiUrl = coordinator.PhoneUrl, tailscaleUrl = coordinator.TailscalePhoneUrl }));
        app.MapGet("/api/phone/qr", (HttpContext context) =>
        {
            // The desktop QR is deliberately Wi-Fi/LAN only. Tailscale has a separate copyable link.
            var url = coordinator.PhoneUrl;
            if (string.IsNullOrWhiteSpace(url) || url.Contains("127.0.0.1", StringComparison.Ordinal)) return Results.NotFound();
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
            var svg = new QRCoder.SvgQRCode(data).GetGraphic(8, "#11130f", "#f1f2eb", false);
            context.Response.Headers["Cache-Control"] = "no-store";
            return Results.Content(svg, "image/svg+xml; charset=utf-8");
        });
        app.MapGet("/api/sounds", () => Results.Ok(coordinator.Library.All.Select(s => View(s, coordinator.Audio.Playback))));
        app.MapGet("/api/marketplace/search", async (string? q, HttpContext context) =>
        {
            try { return Results.Ok(await marketplace.SearchAsync(q, context.RequestAborted)); }
            catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
            catch (HttpRequestException ex) { Log.Warning(ex, "Marketplace search failed"); return Results.Problem("MyInstants is temporarily unavailable. Try again in a moment.", statusCode: 502); }
            catch (InvalidDataException ex) { Log.Warning(ex, "Marketplace search returned invalid data"); return Results.Problem("The sound catalog returned an invalid response.", statusCode: 502); }
            catch (JsonException ex) { Log.Warning(ex, "Marketplace search returned malformed JSON"); return Results.Problem("The sound catalog returned an invalid response.", statusCode: 502); }
        });
        app.MapPost("/api/marketplace/{id}/install", async (string id, HttpContext context) =>
        {
            if (id.Length is < 1 or > 120 || !marketplace.TryGet(id, out var entry)) return Results.NotFound("Search for this sound again before installing it.");
            try
            {
                var bytes = await marketplace.DownloadAsync(entry, coordinator.Settings.MaxUploadBytes, context.RequestAborted);
                await using var stream = new MemoryStream(bytes, writable: false);
                var sound = await coordinator.Library.ImportAsync(stream, entry.Title + ".mp3", context.RequestAborted);
                sound = coordinator.Library.Update(sound.Id, imported => { imported.SourceProvider = "MyInstants"; imported.SourceUrl = entry.PageUrl; });
                Log.Information("Marketplace sound imported {Id} from MyInstants entry {SourceId}", sound.Id, entry.Id);
                return Results.Created($"/api/sounds/{sound.Id}", View(sound, coordinator.Audio.Playback));
            }
            catch (InvalidDataException ex) { return Results.BadRequest(ex.Message); }
            catch (HttpRequestException ex) { Log.Warning(ex, "Marketplace sound download failed {SourceId}", entry.Id); return Results.Problem("MyInstants could not download this sound. Try again later.", statusCode: 502); }
            catch (Exception ex) when (ex is NotSupportedException or NAudio.MmException or System.Runtime.InteropServices.COMException)
            { Log.Warning(ex, "Marketplace sound could not be decoded {SourceId}", entry.Id); return Results.BadRequest("This sound could not be decoded as audio."); }
        });
        app.MapGet("/api/hotkeys", () => Results.Ok(coordinator.Library.HotkeyAssignments));
        app.MapPost("/api/hotkeys/{id:guid}/trigger", (Guid id) =>
        {
            var sound = coordinator.Library.Get(id);
            if (sound is null) return Results.NotFound();
            if (coordinator.Audio.Playback.SoundId == id && sound.Mode == "toggle") coordinator.Stop();
            else coordinator.Play(id);
            return Results.Ok(coordinator.Audio.Playback);
        });
        app.MapGet("/api/sounds/{id:guid}/image", (Guid id) =>
        {
            var sound = coordinator.Library.Get(id);
            if (sound?.ImageFilename is null) return Results.NotFound();
            var path = Path.Combine(coordinator.Storage.ImagesPath, sound.ImageFilename);
            if (!File.Exists(path)) return Results.NotFound();
            var contentType = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".jpg" => "image/jpeg", ".webp" => "image/webp", _ => "application/octet-stream" };
            return Results.File(path, contentType, enableRangeProcessing: false);
        });
        app.MapPost("/api/sounds", async (HttpRequest request) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest("multipart/form-data required");
            try
            {
                var form = await request.ReadFormAsync(); var file = form.Files.GetFile("file");
                if (file is null || file.Length == 0) return Results.BadRequest("Choose an audio file.");
                if (file.Length > coordinator.Settings.MaxUploadBytes) return Results.Problem("File exceeds the upload limit.", statusCode: 413);
                await using var stream = file.OpenReadStream();
                var sound = await coordinator.Library.ImportAsync(stream, file.FileName, request.HttpContext.RequestAborted);
                Log.Information("Sound uploaded {Id}", sound.Id);
                return Results.Created($"/api/sounds/{sound.Id}", View(sound, coordinator.Audio.Playback));
            }
            catch (InvalidDataException ex) { return Results.BadRequest(ex.Message); }
            catch (Exception ex) when (ex is NotSupportedException or NAudio.MmException or System.Runtime.InteropServices.COMException) { Log.Warning(ex, "Audio upload could not be decoded"); return Results.BadRequest("Audio file could not be decoded."); }
        }).DisableAntiforgery();
        app.MapPost("/api/sounds/{id:guid}/image", async (Guid id, HttpRequest request) =>
        {
            var sound = coordinator.Library.Get(id);
            if (sound is null) return Results.NotFound();
            if (!request.HasFormContentType) return Results.BadRequest("multipart/form-data required");
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0) return Results.BadRequest("Choose an image.");
            if (file.Length > 5 * 1024 * 1024) return Results.Problem("Image must be 5 MB or smaller.", statusCode: 413);
            var extension = await DetectImageExtension(file);
            if (extension is null) return Results.BadRequest("Choose a PNG, JPEG or WebP image.");
            var filename = id + "-" + Guid.NewGuid().ToString("N") + extension;
            var path = Path.Combine(coordinator.Storage.ImagesPath, filename);
            await using (var source = file.OpenReadStream())
            await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(target, request.HttpContext.RequestAborted);
            try { coordinator.Library.Update(id, s => s.ImageFilename = filename); }
            catch { File.Delete(path); throw; }
            if (sound.ImageFilename is not null && sound.ImageFilename != filename) File.Delete(Path.Combine(coordinator.Storage.ImagesPath, sound.ImageFilename));
            return Results.Ok(new { imageUrl = $"/api/sounds/{id}/image" });
        }).DisableAntiforgery();
        app.MapDelete("/api/sounds/{id:guid}/image", (Guid id) =>
        {
            var sound = coordinator.Library.Get(id);
            if (sound is null) return Results.NotFound();
            if (sound.ImageFilename is not null)
            {
                coordinator.Library.Update(id, s => s.ImageFilename = null);
                File.Delete(Path.Combine(coordinator.Storage.ImagesPath, sound.ImageFilename));
            }
            return Results.NoContent();
        });
        app.MapPatch("/api/sounds/{id:guid}", (Guid id, SoundPatch patch) =>
        {
            try { return Results.Ok(View(coordinator.UpdateSound(id, s => ApplyPatch(s, patch)), coordinator.Audio.Playback)); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
        });
        app.MapDelete("/api/sounds/{id:guid}", (Guid id) => { try { if (coordinator.Audio.Playback.SoundId == id) coordinator.Stop(); coordinator.Library.Delete(id); return Results.NoContent(); } catch (KeyNotFoundException) { return Results.NotFound(); } });
        app.MapPost("/api/sounds/bulk-delete", (Guid[] ids) =>
        {
            if (ids.Length is < 1 or > 200) return Results.BadRequest("Select between 1 and 200 sounds.");
            try
            {
                if (coordinator.Audio.Playback.SoundId is Guid playing && ids.Contains(playing)) coordinator.Stop();
                coordinator.Library.DeleteMany(ids);
                return Results.NoContent();
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(ex.Message); }
        });
        app.MapPost("/api/sounds/{id:guid}/play", (Guid id) => { try { coordinator.Play(id); return Results.Ok(coordinator.Audio.Playback); } catch (KeyNotFoundException) { return Results.NotFound(); } catch (Exception ex) { return Results.Problem(ex.Message); } });
        app.MapPost("/api/sounds/{id:guid}/stop", (Guid id) => { if (coordinator.Audio.Playback.SoundId == id) coordinator.Stop(); return Results.Ok(coordinator.Audio.Playback); });
        app.MapPost("/api/stop", () => { coordinator.Stop(); return Results.Ok(coordinator.Audio.Playback); });
        app.MapPost("/api/sounds/reorder", (Guid[] ids) => { try { coordinator.Library.Reorder(ids); return Results.Ok(); } catch (ArgumentException ex) { return Results.BadRequest(ex.Message); } });
        app.MapGet("/api/settings", () => Results.Ok(coordinator.SettingsView()));
        app.MapPatch("/api/settings", (SettingsPatch patch) =>
        {
            var oldPort = coordinator.Settings.Port;
            try
            {
                var settings = coordinator.UpdateSettings(s => { if (patch.Port is not null) s.Port = patch.Port.Value; if (patch.ButtonDensity is not null) s.ButtonDensity = patch.ButtonDensity.Value; if (patch.MasterVolume is not null) s.MasterVolume = patch.MasterVolume.Value; if (patch.MicOutputGain is not null) s.MicOutputGain = patch.MicOutputGain.Value; if (patch.UseVirtualMicHeadroom is not null) s.UseVirtualMicHeadroom = patch.UseVirtualMicHeadroom.Value; if (patch.LanAccess is not null) s.LanAccess = patch.LanAccess.Value; if (patch.TailscaleAccess is not null) s.TailscaleAccess = patch.TailscaleAccess.Value; if (patch.PairingToken is not null) s.PairingToken = patch.PairingToken; if (patch.ClearPairingToken) s.PairingToken = null; if (patch.MaxUploadBytes is not null) s.MaxUploadBytes = patch.MaxUploadBytes.Value; if (patch.MonitorLocally is not null) s.MonitorLocally = patch.MonitorLocally.Value; if (patch.ReconnectAudio is not null) s.ReconnectAudio = patch.ReconnectAudio.Value; });
                if (ShouldRestartForPortChange(patch.Port, oldPort, coordinator.ActiveWebPort)) _ = RestartAfterPortChangeAsync();
                return Results.Ok(AppSettingsView.From(settings));
            }
            catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
        });
        app.MapGet("/api/audio/devices", () =>
        {
            try { return Results.Ok(coordinator.Devices.Enumerate(coordinator.Settings.EndpointId ?? coordinator.Audio.EndpointId)); }
            catch (Exception ex) { Log.Error(ex, "Audio endpoint enumeration failed"); return Results.Problem(ex.Message); }
        });
        app.MapPost("/api/audio/device", (DeviceSelection body) => { coordinator.SelectDevice(body.Id); return Results.Ok(new { status = coordinator.Audio.Status, endpoint = coordinator.Audio.EndpointName }); });
        app.MapPost("/api/audio/monitor-device", (DeviceSelection body) => { coordinator.SelectMonitorDevice(body.Id); return Results.Ok(new { monitorEndpointId = coordinator.Settings.MonitorEndpointId }); });
        app.MapPost("/api/audio/test", () => { try { var settings = coordinator.Settings; TestTone.Play(coordinator.Audio, settings.MasterVolume, coordinator.Storage, settings.MonitorLocally, settings.MonitorEndpointId, settings.UseVirtualMicHeadroom); return Results.Ok(); } catch (Exception ex) { return Results.Problem(ex.Message); } });
        app.MapPost("/api/audio/diagnose", async (HttpContext context) =>
        {
            var remote = context.Connection.RemoteIpAddress;
            if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
            if (remote is null || !System.Net.IPAddress.IsLoopback(remote)) return Results.Forbid();
            try { return Results.Ok(await diagnostic.RunAsync(context.RequestAborted)); }
            catch (Exception ex) { Log.Error(ex, "Steam microphone path check failed"); return Results.Problem(ex.Message); }
        });
        app.Map("/ws", async context => { if (context.WebSockets.IsWebSocketRequest) await hub.Accept(context); else context.Response.StatusCode = 400; });
        try { await app.StartAsync(); _app = app; coordinator.SetActiveWebPort(port); StartedOnPort?.Invoke(port); LastError = null; Log.Information("Web server started on port {Port}", port); }
        catch (Exception ex) { LastError = ex.Message; Log.Error(ex, "Web server startup failed"); await app.DisposeAsync(); throw; }
    }
    private async Task RestartAfterPortChangeAsync()
    {
        await Task.Delay(350);
        try { await RestartAsync(); }
        catch (Exception ex) { Log.Error(ex, "Could not restart web server on the requested or fallback port"); }
    }
    public static int FallbackPort(int preferred) => preferred == 6669 ? 6769 : 6669;
    internal static long GetRequestBodyLimit(string method, string path, long configuredUploadBytes)
    {
        var uploadLimit = Math.Clamp(configuredUploadBytes, 1024 * 1024, MaximumUploadBytes);
        if (HttpMethods.IsPost(method))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments is { Length: 4 } &&
                segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
                segments[1].Equals("sounds", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(segments[2], out _) &&
                segments[3].Equals("image", StringComparison.OrdinalIgnoreCase))
                uploadLimit = ImageUploadBytes;
        }
        return uploadLimit + MultipartOverheadBytes;
    }
    public static bool ShouldRestartForPortChange(int? requestedPort, int previousPreference, int activePort)
        => requestedPort is int port && (port != previousPreference || port != activePort);
    public static bool IsPairingAuthorized(System.Net.IPAddress? remote, string? token, string? headerToken, string? queryToken)
    {
        remote = AppCoordinator.NormalizeNetworkAddress(remote);
        if (remote is null || System.Net.IPAddress.IsLoopback(remote) || string.IsNullOrEmpty(token)) return true;
        return string.Equals(headerToken, token, StringComparison.Ordinal) || string.Equals(queryToken, token, StringComparison.Ordinal);
    }
    internal static bool IsRequestHostAllowed(string host, IPAddress? localAddress)
    {
        if (localAddress is null) return false;
        localAddress = AppCoordinator.NormalizeNetworkAddress(localAddress);
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return localAddress is not null && IPAddress.IsLoopback(localAddress);
        return IPAddress.TryParse(host, out var requestedAddress) &&
            AppCoordinator.NormalizeNetworkAddress(requestedAddress)?.Equals(localAddress) == true;
    }
    internal static bool IsSameOrigin(string? origin, string scheme, string requestHost)
    {
        if (string.IsNullOrWhiteSpace(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
            !Uri.TryCreate($"{scheme}://{requestHost}", UriKind.Absolute, out var requestUri)) return false;
        return originUri.UserInfo.Length == 0 && originUri.Scheme.Equals(requestUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            originUri.Authority.Equals(requestUri.Authority, StringComparison.OrdinalIgnoreCase) &&
            originUri.AbsolutePath == "/" && originUri.Query.Length == 0 && originUri.Fragment.Length == 0;
    }
    private static bool IsAddressInUse(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse) return true;
        return false;
    }
    public async Task StopAsync() { var app = _app; _app = null; coordinator.SetActiveWebPort(null); if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); } }
    public async Task RestartAsync() { await StopAsync(); await StartAsync(); }
    private object View(Sound s, PlaybackState p)
    {
        var imageUrl = s.ImageFilename is null ? "/assets/logo.png" : $"/api/sounds/{s.Id}/image?v={Uri.EscapeDataString(s.ImageFilename)}";
        if (s.ImageFilename is not null && !string.IsNullOrWhiteSpace(coordinator.Settings.PairingToken)) imageUrl += "&token=" + Uri.EscapeDataString(coordinator.Settings.PairingToken);
        return new { s.Id, s.Name, s.SourceFilename, s.SourceProvider, s.SourceUrl, s.SortOrder, s.OutputGain, s.StartSeconds, s.EndSeconds, s.SourceDurationSeconds, duration = s.PlayDuration, s.Mode, s.Hotkey, s.Icon, s.ButtonLabel, s.CreatedUtc, imageUrl, playing = p.SoundId == s.Id, progress = p.SoundId == s.Id && s.PlayDuration > 0 ? Math.Clamp((p.PositionSeconds - s.StartSeconds) / s.PlayDuration, 0, 1) : 0 };
    }
    private static void ApplyPatch(Sound s, SoundPatch p) { if (p.Name is not null) s.Name = p.Name; if (p.Volume is not null) s.Volume = p.Volume.Value; if (p.OutputGain is not null) s.OutputGain = p.OutputGain.Value; if (p.StartSeconds is not null) s.StartSeconds = p.StartSeconds.Value; if (p.EndSeconds.ValueKind != JsonValueKind.Undefined) s.EndSeconds = p.EndSeconds.ValueKind == JsonValueKind.Null ? null : p.EndSeconds.GetDouble(); if (p.Mode is not null) s.Mode = p.Mode; if (p.Hotkey is not null) s.Hotkey = p.Hotkey; if (p.Icon is not null) s.Icon = p.Icon; if (p.ButtonLabel is not null) s.ButtonLabel = p.ButtonLabel; }
    private static async Task<string?> DetectImageExtension(IFormFile file)
    {
        await using var stream = file.OpenReadStream();
        var head = new byte[12];
        var read = 0;
        while (read < head.Length)
        {
            var count = await stream.ReadAsync(head.AsMemory(read));
            if (count == 0) break;
            read += count;
        }
        if (read >= 8 && head.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (read >= 3 && head[0] == 0xff && head[1] == 0xd8 && head[2] == 0xff) return ".jpg";
        if (read >= 12 && head.AsSpan(0, 4).SequenceEqual("RIFF"u8) && head.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return ".webp";
        return null;
    }
}
public record SoundPatch(string? Name, float? Volume, float? OutputGain, double? StartSeconds, JsonElement EndSeconds, string? Mode, string? Icon, string? ButtonLabel, string? Hotkey = null);
public record SettingsPatch(int? ButtonDensity, float? MasterVolume, float? MicOutputGain, bool? LanAccess, string? PairingToken, long? MaxUploadBytes, bool? MonitorLocally, bool? ReconnectAudio, int? Port, bool ClearPairingToken = false, bool? TailscaleAccess = null, bool? UseVirtualMicHeadroom = null);
public record DeviceSelection(string? Id);
