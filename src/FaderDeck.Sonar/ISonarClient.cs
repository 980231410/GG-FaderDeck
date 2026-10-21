using FaderDeck.Domain;

namespace FaderDeck.Sonar;

public interface ISonarClient
{
    Task<SonarSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChannelVolume>> GetClassicVolumesAsync(
        CancellationToken cancellationToken = default);

    Task<VolumeState> SetChannelVolumeAsync(
        string channel,
        float volume,
        CancellationToken cancellationToken = default);

    Task<VolumeState> SetChannelMutedAsync(
        string channel,
        bool muted,
        CancellationToken cancellationToken = default);

    Task<ChannelBinding> SetChannelBindingAsync(
        string channel,
        string deviceId,
        CancellationToken cancellationToken = default);

    Task SelectEqPresetAsync(
        string presetId,
        CancellationToken cancellationToken = default);

    Task RouteApplicationAsync(
        string dataFlow,
        string targetDeviceId,
        int processId,
        CancellationToken cancellationToken = default);

    Task<StreamerSnapshot> GetStreamerSnapshotAsync(CancellationToken cancellationToken = default);

    Task<string> GetModeAsync(CancellationToken cancellationToken = default);

    Task SetModeAsync(string mode, CancellationToken cancellationToken = default);

    Task<VolumeState> SetStreamerVolumeAsync(
        string mix,
        string channel,
        float volume,
        CancellationToken cancellationToken = default);

    Task<VolumeState> SetStreamerMutedAsync(
        string mix,
        string channel,
        bool muted,
        CancellationToken cancellationToken = default);

    Task<StreamerSource> SetStreamSourceEnabledAsync(
        string mix,
        string role,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<StreamerOutput> SetStreamerOutputDeviceAsync(
        string mix,
        string deviceId,
        CancellationToken cancellationToken = default);

    Task<bool> SetStreamMonitoringEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<DeviceOutFeature> SetDeviceOutVolumeAsync(
        string feature,
        float volume,
        CancellationToken cancellationToken = default);

    Task<DeviceOutFeature> SetDeviceOutMutedAsync(
        string feature,
        bool muted,
        CancellationToken cancellationToken = default);

    Task SelectAudioPathAsync(string feature, CancellationToken cancellationToken = default);
}
