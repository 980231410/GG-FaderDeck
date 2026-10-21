namespace FaderDeck.Domain;

public sealed record VolumeState(float Volume, bool Muted);

public sealed record ChannelVolume(string Channel, VolumeState State);

public sealed record ChannelVolumeUpdate(string Channel, float? Volume, bool? Muted);

public sealed record AudioDevice(
    string FriendlyName,
    string Id,
    string DataFlow,
    string Role,
    string State,
    bool IsVirtual);

public sealed record ChannelBinding(
    string Channel,
    string DeviceId,
    bool IsRunning);

public sealed record AudioSession(
    string Id,
    string ProcessName,
    int ProcessId,
    string DisplayName,
    string State,
    bool IsSystemSound,
    bool RoutingErrorDetected);

public sealed record DeviceSessions(
    string DeviceId,
    string Role,
    string DataFlow,
    IReadOnlyList<AudioSession> Sessions);

public sealed record EqPreset(
    string Id,
    string Name,
    string Channel,
    bool IsFavorite,
    int FavoritePosition);

public sealed record SelectedEqPreset(
    string Id,
    string Name,
    string Channel);

public sealed record SonarSnapshot(
    string Mode,
    IReadOnlyList<ChannelVolume> Volumes,
    IReadOnlyList<AudioDevice> Devices,
    IReadOnlyList<ChannelBinding> Bindings,
    IReadOnlyList<DeviceSessions> DeviceSessions,
    IReadOnlyList<EqPreset> EqPresets,
    IReadOnlyList<SelectedEqPreset> SelectedEqPresets,
    DateTimeOffset CapturedAt,
    StreamerSnapshot? Streamer = null);

/// <summary>One volume cell of a Streamer-mode mix: the channel plus the mix it belongs to.</summary>
public sealed record StreamerVolume(string Mix, string Channel, VolumeState State);

/// <summary>Whether a channel feeds a Streamer-mode mix.</summary>
public sealed record StreamerSource(string Mix, string Role, bool IsEnabled);

/// <summary>The endpoint a Streamer-mode mix renders to.</summary>
public sealed record StreamerOutput(string Mix, string DeviceId, bool IsRunning);

public sealed record DeviceOutFeature(string Feature, VolumeState State);

/// <summary>
/// Streamer mode keeps two independent mixes — "streaming" (what the audience hears) and
/// "monitoring" (what the user hears) — over the same Sonar channels.
/// </summary>
public sealed record StreamerSnapshot(
    string Mode,
    IReadOnlyList<StreamerVolume> Volumes,
    IReadOnlyList<StreamerSource> Sources,
    IReadOnlyList<StreamerOutput> Outputs,
    bool IsMonitoringEnabled,
    bool IsMonitoringLocked,
    string AudioPath,
    IReadOnlyList<DeviceOutFeature> DeviceOut);
