using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Text;
using FaderDeck.Domain;

namespace FaderDeck.Sonar;

public sealed class SonarClient : ISonarClient, IDisposable
{
    private static readonly HashSet<string> VolumeChannels =
        ["master", "game", "chatRender", "chatCapture", "media", "aux"];

    private static readonly HashSet<string> BindingChannels =
        ["game", "chat", "media", "aux", "mic"];

    private static readonly string[] EqChannels = ["game", "chatRender", "chatCapture", "media", "aux"];
    private static readonly TimeSpan EqPresetCacheLifetime = TimeSpan.FromMinutes(1);

    // Streamer mode exposes the same Sonar channels twice, once per mix.  These identifiers are
    // the ones both /volumeSettings/streamer and /streamRedirections use.
    private static readonly string[] StreamMixes = ["streaming", "monitoring"];
    private static readonly string[] StreamRoles = ["game", "chatRender", "chatCapture", "media", "aux"];

    // /streamRedirections also carries the microphone redirection, which has no mix branches
    // of its own but does expose a device picker in GG.
    private static readonly string[] StreamOutputTargets = ["streaming", "monitoring", "mic"];
    private static readonly string[] StreamModes = ["classic", "stream"];

    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, (IReadOnlyList<EqPreset> Presets, DateTimeOffset UpdatedAt)> _eqPresetCache = [];

    public SonarClient(Uri baseAddress, TimeSpan? timeout = null)
    {
        EnsureLoopbackHttpAddress(baseAddress);
        var handler = new HttpClientHandler { UseProxy = false };
        _httpClient = new HttpClient(handler)
        {
            BaseAddress = baseAddress,
            Timeout = timeout ?? TimeSpan.FromSeconds(3)
        };
    }

    public Task<SonarSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        GetSnapshotAsync(includeHeavyData: true, cancellationToken);

    public async Task<SonarSnapshot> GetSnapshotAsync(
        bool includeHeavyData,
        CancellationToken cancellationToken = default)
        => await GetSnapshotAsync(includeHeavyData, includeHeavyData, cancellationToken).ConfigureAwait(false);

    public async Task<SonarSnapshot> GetSnapshotAsync(
        bool includeRoutingData,
        bool includeEqPresets,
        CancellationToken cancellationToken = default)
    {
        var modeTask = GetAsync<string>("/mode", cancellationToken);
        var volumesTask = GetAsync<VolumeSettingsDocument>("/volumeSettings/classic", cancellationToken);
        var devicesTask = GetAsync<List<AudioDeviceDto>>("/audioDevices", cancellationToken);
        var bindingsTask = GetAsync<List<ChannelBindingDto>>("/classicRedirections", cancellationToken);
        var selectedPresetsTask = GetAsync<List<SelectedEqPresetDto>>("/configs/selected", cancellationToken);
        var routingTask = includeRoutingData
            ? GetAsync<List<DeviceSessionsDto>>("/AudioDeviceRouting", cancellationToken)
            : Task.FromResult(new List<DeviceSessionsDto>());
        var presetsTask = includeEqPresets
            ? GetAllEqPresetsAsync(cancellationToken)
            : Task.FromResult<IReadOnlyList<EqPreset>>(Array.Empty<EqPreset>());

        var streamerTask = GetStreamerSnapshotSafeAsync(cancellationToken);
        await Task.WhenAll(modeTask, volumesTask, devicesTask, bindingsTask, routingTask, presetsTask, selectedPresetsTask, streamerTask).ConfigureAwait(false);
        return new SonarSnapshot(
            await modeTask.ConfigureAwait(false),
            MapVolumes(await volumesTask.ConfigureAwait(false)),
            (await devicesTask.ConfigureAwait(false)).Select(MapDevice).ToArray(),
            (await bindingsTask.ConfigureAwait(false)).Select(MapBinding).ToArray(),
            (await routingTask.ConfigureAwait(false)).Select(MapDeviceSessions).ToArray(),
            await presetsTask.ConfigureAwait(false),
            (await selectedPresetsTask.ConfigureAwait(false)).Select(MapSelectedEqPreset).ToArray(),
            DateTimeOffset.UtcNow,
            await streamerTask.ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<ChannelVolume>> GetClassicVolumesAsync(
        CancellationToken cancellationToken = default)
    {
        var document = await GetAsync<VolumeSettingsDocument>("/volumeSettings/classic", cancellationToken)
            .ConfigureAwait(false);
        return MapVolumes(document);
    }

    public async Task<VolumeState> SetChannelVolumeAsync(
        string channel,
        float volume,
        CancellationToken cancellationToken = default)
    {
        EnsureVolumeChannel(channel);
        if (volume is < 0 or > 1 || float.IsNaN(volume))
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be between 0 and 1.");
        }

        var value = volume.ToString("R", CultureInfo.InvariantCulture);
        await PutAsync($"/volumeSettings/classic/{channel}/Volume/{value}", cancellationToken)
            .ConfigureAwait(false);
        return await ReadClassicVolumeAsync(channel, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VolumeState> SetChannelMutedAsync(
        string channel,
        bool muted,
        CancellationToken cancellationToken = default)
    {
        EnsureVolumeChannel(channel);
        await PutAsync($"/volumeSettings/classic/{channel}/Mute/{muted.ToString().ToLowerInvariant()}", cancellationToken)
            .ConfigureAwait(false);
        return await ReadClassicVolumeAsync(channel, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChannelBinding> SetChannelBindingAsync(
        string channel,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (!BindingChannels.Contains(channel))
        {
            throw new ArgumentException($"Unknown Sonar binding channel '{channel}'.", nameof(channel));
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("A target device ID is required.", nameof(deviceId));
        }

        var response = await PutAsync<ChannelBindingDto>(
            $"/classicRedirections/{channel}/deviceId/{Uri.EscapeDataString(deviceId)}",
            cancellationToken).ConfigureAwait(false);
        return MapBinding(response);
    }

    public async Task SelectEqPresetAsync(string presetId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(presetId, out _))
        {
            throw new ArgumentException("A valid EQ preset ID is required.", nameof(presetId));
        }

        // GG submits this as a JSON PUT, then refreshes /configs/selected.  Matching both steps
        // avoids treating a locally changed ComboBox selection as a completed Sonar configuration switch.
        await PutJsonAsync($"/configs/{Uri.EscapeDataString(presetId)}/select", cancellationToken)
            .ConfigureAwait(false);
        var selected = await GetAsync<List<SelectedEqPresetDto>>("/configs/selected", cancellationToken)
            .ConfigureAwait(false);
        if (!selected.Any(config => string.Equals(config.Id, presetId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Sonar did not confirm the EQ preset switch.");
        }
    }

    public Task RouteApplicationAsync(
        string dataFlow,
        string targetDeviceId,
        int processId,
        CancellationToken cancellationToken = default)
    {
        if (dataFlow is not ("render" or "capture"))
        {
            throw new ArgumentException("Data flow must be 'render' or 'capture'.", nameof(dataFlow));
        }

        if (string.IsNullOrWhiteSpace(targetDeviceId))
        {
            throw new ArgumentException("A target device ID is required.", nameof(targetDeviceId));
        }

        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "A positive process ID is required.");
        }

        return PutAsync(
            $"/AudioDeviceRouting/{dataFlow}/{Uri.EscapeDataString(targetDeviceId)}/{processId}",
            cancellationToken);
    }

    public async Task<StreamerSnapshot> GetStreamerSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var modeTask = GetAsync<string>("/mode", cancellationToken);
        var volumesTask = GetAsync<StreamerVolumeDocument>("/volumeSettings/streamer", cancellationToken);
        var redirectionsTask = GetAsync<List<StreamRedirectionDto>>("/streamRedirections", cancellationToken);
        var monitoringTask = GetAsync<bool>("/streamRedirections/isStreamMonitoringEnabled", cancellationToken);
        var lockedTask = GetAsync<bool>("/streamRedirections/isStreamMonitoringLocked", cancellationToken);
        var audioPathTask = GetAsync<string>("/deviceOut/audioPath", cancellationToken);
        var deviceOutTask = GetAsync<DeviceOutDocument>("/deviceOut", cancellationToken);
        await Task.WhenAll(
            modeTask,
            volumesTask,
            redirectionsTask,
            monitoringTask,
            lockedTask,
            audioPathTask,
            deviceOutTask).ConfigureAwait(false);

        var document = await volumesTask.ConfigureAwait(false);
        var redirections = (await redirectionsTask.ConfigureAwait(false))
            .Where(redirection => StreamOutputTargets.Contains(redirection.StreamRedirectionId, StringComparer.Ordinal))
            .ToArray();

        var volumes = new List<StreamerVolume>();
        foreach (var mix in StreamMixes)
        {
            if (TryGetMixState(document.Masters?.Stream, mix, out var master))
            {
                volumes.Add(new StreamerVolume(mix, "master", master));
            }

            foreach (var role in StreamRoles)
            {
                if (TryGetMixState(document.Devices?.GetValueOrDefault(role)?.Stream, mix, out var channelState))
                {
                    volumes.Add(new StreamerVolume(mix, role, channelState));
                }
            }
        }

        return new StreamerSnapshot(
            await modeTask.ConfigureAwait(false),
            volumes,
            redirections
                .SelectMany(redirection => (redirection.Status ?? [])
                    .Where(status => !string.IsNullOrEmpty(status.Role))
                    .Select(status => new StreamerSource(redirection.StreamRedirectionId!, status.Role!, status.IsEnabled)))
                .ToArray(),
            redirections
                .Select(redirection => new StreamerOutput(
                    redirection.StreamRedirectionId!,
                    redirection.DeviceId ?? string.Empty,
                    redirection.IsRunning))
                .ToArray(),
            await monitoringTask.ConfigureAwait(false),
            await lockedTask.ConfigureAwait(false),
            await audioPathTask.ConfigureAwait(false),
            (await deviceOutTask.ConfigureAwait(false)).DeviceOutSettings?
                .Where(setting => !string.IsNullOrWhiteSpace(setting.Feature))
                .Select(setting => new DeviceOutFeature(setting.Feature!, new VolumeState(setting.Volume, setting.Mute)))
                .ToArray() ?? []);
    }

    public async Task<string> GetModeAsync(CancellationToken cancellationToken = default)
    {
        return await GetAsync<string>("/mode", cancellationToken).ConfigureAwait(false);
    }

    public async Task SetModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        if (!StreamModes.Contains(mode, StringComparer.Ordinal))
        {
            throw new ArgumentException("Sonar mode must be 'classic' or 'stream'.", nameof(mode));
        }

        var confirmed = await PutAsync<string>($"/mode/{mode}", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(confirmed, mode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Sonar reported '{confirmed}' instead of '{mode}' after the mode switch.");
        }
    }

    public async Task<VolumeState> SetStreamerVolumeAsync(
        string mix,
        string channel,
        float volume,
        CancellationToken cancellationToken = default)
    {
        EnsureStreamTarget(mix, channel);
        EnsureVolumeRange(volume);
        var value = volume.ToString("R", CultureInfo.InvariantCulture);
        await PutAsync($"/volumeSettings/streamer/{mix}/{channel}/volume/{value}", cancellationToken)
            .ConfigureAwait(false);
        return await ReadStreamerVolumeAsync(mix, channel, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VolumeState> SetStreamerMutedAsync(
        string mix,
        string channel,
        bool muted,
        CancellationToken cancellationToken = default)
    {
        EnsureStreamTarget(mix, channel);
        await PutAsync($"/volumeSettings/streamer/{mix}/{channel}/isMuted/{ToPathBoolean(muted)}", cancellationToken)
            .ConfigureAwait(false);
        return await ReadStreamerVolumeAsync(mix, channel, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StreamerSource> SetStreamSourceEnabledAsync(
        string mix,
        string role,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        EnsureStreamTarget(mix, role);
        var confirmed = await PutAsync<StreamRedirectionDto>(
            $"/streamRedirections/{mix}/redirections/{role}/isEnabled/{ToPathBoolean(enabled)}",
            cancellationToken).ConfigureAwait(false);
        var status = confirmed.Status?.FirstOrDefault(entry => string.Equals(entry.Role, role, StringComparison.Ordinal));
        return status is null
            ? throw new InvalidDataException($"Sonar did not confirm the '{role}' routing change.")
            : new StreamerSource(mix, role, status.IsEnabled);
    }

    public async Task<StreamerOutput> SetStreamerOutputDeviceAsync(
        string mix,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (!StreamOutputTargets.Contains(mix, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "Streamer output must be 'streaming', 'monitoring', or 'mic'.",
                nameof(mix));
        }

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("A device id is required.", nameof(deviceId));
        }

        var confirmed = await PutAsync<StreamRedirectionDto>(
            $"/streamRedirections/{mix}/deviceId/{Uri.EscapeDataString(deviceId)}",
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(confirmed.StreamRedirectionId, mix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Sonar did not confirm the '{mix}' output device change.");
        }

        return new StreamerOutput(mix, confirmed.DeviceId ?? deviceId, confirmed.IsRunning);
    }

    public Task<bool> SetStreamMonitoringEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        PutAsync<bool>(
            $"/streamRedirections/isStreamMonitoringEnabled/{ToPathBoolean(enabled)}",
            cancellationToken);

    public async Task<DeviceOutFeature> SetDeviceOutVolumeAsync(
        string feature,
        float volume,
        CancellationToken cancellationToken = default)
    {
        EnsureDeviceOutFeature(feature);
        EnsureVolumeRange(volume);
        var value = volume.ToString("R", CultureInfo.InvariantCulture);
        await PutAsync($"/deviceOut/volume/{Uri.EscapeDataString(feature)}/{value}", cancellationToken)
            .ConfigureAwait(false);
        return await ReadDeviceOutFeatureAsync(feature, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceOutFeature> SetDeviceOutMutedAsync(
        string feature,
        bool muted,
        CancellationToken cancellationToken = default)
    {
        EnsureDeviceOutFeature(feature);
        await PutAsync($"/deviceOut/mute/{Uri.EscapeDataString(feature)}/{ToPathBoolean(muted)}", cancellationToken)
            .ConfigureAwait(false);
        return await ReadDeviceOutFeatureAsync(feature, cancellationToken).ConfigureAwait(false);
    }

    public async Task SelectAudioPathAsync(string feature, CancellationToken cancellationToken = default)
    {
        EnsureDeviceOutFeature(feature);
        await PutAsync($"/deviceOut/audioPath/{Uri.EscapeDataString(feature)}", cancellationToken)
            .ConfigureAwait(false);
        var confirmed = await GetAsync<string>("/deviceOut/audioPath", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(confirmed, feature, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Sonar reported '{confirmed}' instead of '{feature}' after the output path switch.");
        }
    }

    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// Streamer mode arrived with Sonar 1.104.  A missing endpoint must not take down the
    /// classic panel, so a failed probe degrades to <c>null</c> instead of failing the refresh.
    /// </summary>
    private async Task<StreamerSnapshot?> GetStreamerSnapshotSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetStreamerSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                          exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private async Task<VolumeState> ReadStreamerVolumeAsync(
        string mix,
        string channel,
        CancellationToken cancellationToken)
    {
        var document = await GetAsync<StreamerVolumeDocument>("/volumeSettings/streamer", cancellationToken)
            .ConfigureAwait(false);
        var states = channel == "master"
            ? document.Masters?.Stream
            : document.Devices?.GetValueOrDefault(channel)?.Stream;
        return TryGetMixState(states, mix, out var state)
            ? state
            : throw new InvalidDataException($"Sonar did not return the '{mix}' volume for channel '{channel}'.");
    }

    private async Task<DeviceOutFeature> ReadDeviceOutFeatureAsync(
        string feature,
        CancellationToken cancellationToken)
    {
        var document = await GetAsync<DeviceOutDocument>("/deviceOut", cancellationToken).ConfigureAwait(false);
        var setting = document.DeviceOutSettings?.FirstOrDefault(
            entry => string.Equals(entry.Feature, feature, StringComparison.OrdinalIgnoreCase));
        return setting is null
            ? throw new InvalidDataException($"Sonar did not return the '{feature}' output state.")
            : new DeviceOutFeature(feature, new VolumeState(setting.Volume, setting.Mute));
    }

    private static bool TryGetMixState(
        Dictionary<string, VolumeStateDto?>? states,
        string mix,
        out VolumeState state)
    {
        if (states is not null && states.TryGetValue(mix, out var dto) && dto is not null)
        {
            state = dto.ToDomain();
            return true;
        }

        state = new VolumeState(0f, false);
        return false;
    }

    private static bool IsStreamMix(string? id) =>
        id is not null && StreamMixes.Contains(id, StringComparer.Ordinal);

    private static void EnsureStreamTarget(string mix, string channel)
    {
        if (!IsStreamMix(mix))
        {
            throw new ArgumentException($"Unknown Streamer mix '{mix}'.", nameof(mix));
        }

        if (channel != "master" && !StreamRoles.Contains(channel, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown Streamer channel '{channel}'.", nameof(channel));
        }
    }

    private static void EnsureDeviceOutFeature(string feature)
    {
        if (string.IsNullOrWhiteSpace(feature))
        {
            throw new ArgumentException("A device output feature is required.", nameof(feature));
        }
    }

    private static void EnsureVolumeRange(float volume)
    {
        if (volume is < 0 or > 1 || float.IsNaN(volume))
        {
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be between 0 and 1.");
        }
    }

    private static string ToPathBoolean(bool value) => value ? "true" : "false";

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Sonar returned an empty JSON response for {path}.");
    }

    private async Task PutAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task PutJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<T> PutAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, path);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Sonar returned an empty JSON response for {path}.");
    }

    private async Task<VolumeState> ReadClassicVolumeAsync(string channel, CancellationToken cancellationToken)
    {
        var document = await GetAsync<VolumeSettingsDocument>("/volumeSettings/classic", cancellationToken)
            .ConfigureAwait(false);
        var state = channel == "master"
            ? document.Masters?.Classic
            : document.Devices?.GetValueOrDefault(channel)?.Classic;
        return state?.ToDomain()
            ?? throw new InvalidDataException($"Sonar did not return volume state for channel '{channel}'.");
    }

    private static void EnsureVolumeChannel(string channel)
    {
        if (!VolumeChannels.Contains(channel))
        {
            throw new ArgumentException($"Unknown Sonar volume channel '{channel}'.", nameof(channel));
        }
    }

    private static IReadOnlyList<ChannelVolume> MapVolumes(VolumeSettingsDocument document)
    {
        var result = new List<ChannelVolume>();
        if (document.Masters?.Classic is not null)
        {
            result.Add(new ChannelVolume("master", document.Masters.Classic.ToDomain()));
        }

        if (document.Devices is not null)
        {
            result.AddRange(document.Devices
                .Where(pair => pair.Value.Classic is not null)
                .Select(pair => new ChannelVolume(pair.Key, pair.Value.Classic!.ToDomain())));
        }

        return result;
    }

    private static AudioDevice MapDevice(AudioDeviceDto device) => new(
        device.FriendlyName ?? "Unknown device",
        device.Id ?? string.Empty,
        device.DataFlow ?? "unknown",
        device.Role ?? "none",
        device.State ?? "unknown",
        device.IsVad);

    public async Task<IReadOnlyList<EqPreset>> GetEqPresetsAsync(
        string channel,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        if (!EqChannels.Contains(channel, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown EQ channel '{channel}'.", nameof(channel));
        }

        if (!forceRefresh && _eqPresetCache.TryGetValue(channel, out var cached) &&
            DateTimeOffset.UtcNow - cached.UpdatedAt < EqPresetCacheLifetime)
        {
            return cached.Presets;
        }

        var presets = (await GetAsync<List<EqPresetDto>>($"/presets/{channel}", cancellationToken).ConfigureAwait(false))
            .Select(preset => MapEqPreset(preset, channel))
            .ToArray();
        _eqPresetCache[channel] = (presets, DateTimeOffset.UtcNow);
        return presets;
    }

    private async Task<IReadOnlyList<EqPreset>> GetAllEqPresetsAsync(CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(EqChannels.Select(channel => GetEqPresetsAsync(channel, cancellationToken)))
            .ConfigureAwait(false);
        return results.SelectMany(presets => presets).ToArray();
    }

    private static EqPreset MapEqPreset(EqPresetDto preset, string channel) => new(
        preset.Id ?? string.Empty,
        preset.Name ?? "Unknown EQ",
        channel,
        preset.IsFavorite,
        preset.FavoritePosition);

    private static SelectedEqPreset MapSelectedEqPreset(SelectedEqPresetDto preset) => new(
        preset.Id ?? string.Empty,
        preset.Name ?? "Unknown EQ",
        preset.VirtualAudioDevice ?? string.Empty);

    private static ChannelBinding MapBinding(ChannelBindingDto binding) => new(
        binding.Id ?? "unknown",
        binding.DeviceId ?? string.Empty,
        binding.IsRunning);

    private static DeviceSessions MapDeviceSessions(DeviceSessionsDto routing) => new(
        routing.DeviceId ?? string.Empty,
        routing.Role ?? "none",
        routing.DataFlow ?? "unknown",
        (routing.AudioSessions ?? []).Select(session => new AudioSession(
            session.Id ?? string.Empty,
            session.ProcessName ?? "Unknown",
            session.ProcessId,
            session.DisplayName ?? session.ProcessName ?? "Unknown",
            session.State ?? "unknown",
            session.IsSystemSound,
            session.RoutingErrorDetected)).ToArray());

    private static void EnsureLoopbackHttpAddress(Uri address)
    {
        if (address.Scheme != Uri.UriSchemeHttp ||
            !System.Net.IPAddress.TryParse(address.Host, out var ip) ||
            !System.Net.IPAddress.IsLoopback(ip))
        {
            throw new ArgumentException("The Sonar API address must be an HTTP loopback address.", nameof(address));
        }
    }

    internal sealed record VolumeSettingsDocument(
        [property: JsonPropertyName("masters")] VolumeBranches? Masters,
        [property: JsonPropertyName("devices")] Dictionary<string, VolumeBranches>? Devices);

    internal sealed record VolumeBranches(
        [property: JsonPropertyName("classic")] VolumeStateDto? Classic);

    internal sealed record VolumeStateDto(
        [property: JsonPropertyName("volume")] float Volume,
        [property: JsonPropertyName("muted")] bool Muted)
    {
        public VolumeState ToDomain() => new(Volume, Muted);
    }

    internal sealed record AudioDeviceDto(
        [property: JsonPropertyName("friendlyName")] string? FriendlyName,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("dataFlow")] string? DataFlow,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("isVad")] bool IsVad);

    internal sealed record ChannelBindingDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("deviceId")] string? DeviceId,
        [property: JsonPropertyName("isRunning")] bool IsRunning);

    internal sealed record EqPresetDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("isFavorite")] bool IsFavorite,
        [property: JsonPropertyName("favoritePosition")] int FavoritePosition);

    internal sealed record SelectedEqPresetDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("virtualAudioDevice")] string? VirtualAudioDevice);

    internal sealed record DeviceSessionsDto(
        [property: JsonPropertyName("deviceId")] string? DeviceId,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("dataFlow")] string? DataFlow,
        [property: JsonPropertyName("audioSessions")] List<AudioSessionDto>? AudioSessions);

    internal sealed record AudioSessionDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("processName")] string? ProcessName,
        [property: JsonPropertyName("processId")] int ProcessId,
        [property: JsonPropertyName("displayName")] string? DisplayName,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("isSystemSound")] bool IsSystemSound,
        [property: JsonPropertyName("routingErrorDetected")] bool RoutingErrorDetected);

    internal sealed record StreamerVolumeDocument(
        [property: JsonPropertyName("masters")] StreamerMastersDto? Masters,
        [property: JsonPropertyName("devices")] Dictionary<string, StreamerChannelBranchesDto>? Devices);

    internal sealed record StreamerMastersDto(
        [property: JsonPropertyName("stream")] Dictionary<string, VolumeStateDto?>? Stream,
        [property: JsonPropertyName("classic")] VolumeStateDto? Classic);

    internal sealed record StreamerChannelBranchesDto(
        [property: JsonPropertyName("stream")] Dictionary<string, VolumeStateDto?>? Stream,
        [property: JsonPropertyName("classic")] VolumeStateDto? Classic);

    internal sealed record StreamRedirectionDto(
        [property: JsonPropertyName("streamRedirectionId")] string? StreamRedirectionId,
        [property: JsonPropertyName("deviceId")] string? DeviceId,
        [property: JsonPropertyName("status")] List<StreamRedirectionStatusDto>? Status,
        [property: JsonPropertyName("isRunning")] bool IsRunning);

    internal sealed record StreamRedirectionStatusDto(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("isEnabled")] bool IsEnabled);

    internal sealed record DeviceOutDocument(
        [property: JsonPropertyName("selectedDeviceOut")] string? SelectedDeviceOut,
        [property: JsonPropertyName("deviceOutSettings")] List<DeviceOutSettingDto>? DeviceOutSettings);

    internal sealed record DeviceOutSettingDto(
        [property: JsonPropertyName("volume")] float Volume,
        [property: JsonPropertyName("mute")] bool Mute,
        [property: JsonPropertyName("feature")] string? Feature);
}
