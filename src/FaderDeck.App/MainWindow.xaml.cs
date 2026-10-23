using System.Windows;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FaderDeck.Application;
using FaderDeck.Domain;
using FaderDeck.Sonar;
using MediaColor = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.Brush;
using WpfPoint = System.Windows.Point;

namespace FaderDeck.App;

public partial class MainWindow : Window
{
    private static readonly IReadOnlyDictionary<string, string> ChannelNames = new Dictionary<string, string>
    {
        ["master"] = "主音量",
        ["game"] = "游戏",
        ["chatRender"] = "聊天",
        ["chatCapture"] = "麦克风",
        ["media"] = "媒体",
        ["aux"] = "辅助"
    };

    private readonly SonarStateService _stateService = new(new CompositeSonarDiscovery([
        new GgCoreDiscovery(),
        new TcpTableSonarDiscovery()
    ]));
    private SonarClient? _sonarClient;
    private readonly DispatcherTimer _syncTimer;
    private readonly DispatcherTimer _volumeFallbackTimer;
    private readonly SonarEventClient _sonarEventClient = new();
    private readonly WindowsAudioEndpointVolume _endpointVolume = new();
    private const string ClassicMode = "classic";
    private const string StreamMode = "stream";
    private const string StreamingMix = "streaming";
    private const string MonitoringMix = "monitoring";
    private const string MicMix = "mic";

    private readonly VolumeWriteCoordinator _volumeWriter;
    private readonly VolumeWriteCoordinator _streamerWriter;
    private readonly Dictionary<string, ChannelRow> _channelRows = [];
    private readonly Dictionary<string, StreamerChannelRow> _streamerRows = [];
    private readonly HashSet<string> _volumeDragChannels = [];
    private readonly Dictionary<string, int> _masterDragBaseline = new(StringComparer.Ordinal);
    private double _masterDragStartPercent = -1d;
    private readonly Dictionary<string, int> _classicMasterBaseline = new(StringComparer.Ordinal);
    private double _classicMasterDragStartPercent = -1d;
    private DateTimeOffset _lastModeCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _modeSwitchGuardUntil = DateTimeOffset.MinValue;
    private readonly HashSet<string> _loadedEqChannels = [];
    private readonly HashSet<string> _loadingEqChannels = [];
    private readonly HashSet<string> _loadingStreamerEq = [];
    private IReadOnlyList<StreamSourceChip> _streamSources = [];
    private StreamerSnapshot? _lastStreamer;
    private IReadOnlyList<AudioDevice> _deviceCatalog = [];
    private TrackDragSession? _trackDrag;
    private Uri? _eventBaseAddress;
    private string _streamerMix = MonitoringMix;
    private DateTimeOffset _monitoringGuardUntil = DateTimeOffset.MinValue;
    private string _monitoringDeviceId = string.Empty;
    private string _micDeviceId = string.Empty;
    private string _currentMode = ClassicMode;
    private bool _hasStreamerData;
    private bool _isApplyingStreamerState;
    private bool _isApplyingStreamerEq;
    private bool _isLoading;
    private bool _isDeferredLoading;
    private bool _hasLoadedHeavyData;
    private bool _isFallbackLoading;
    private bool _isWriting;
    private bool _isDragging;
    private bool _isDeviceDropDownOpen;
    private bool _isApplyingRemoteVolume;
    private WpfPoint _dragStartPoint;
    private FrameworkElement? _draggedElement;
    private RoutingLane? _dropTargetLane;
    private Border? _dropTargetBorder;
    private bool _isFinishingTrackDrag;
    private bool _trackDragHasWindowCapture;

    public bool AllowClose { get; set; }

    public bool HideOnDeactivate { get; set; } = true;

    public MainWindow()
    {
        InitializeComponent();
        _volumeWriter = new VolumeWriteCoordinator(WriteVolumeAsync);
        _volumeWriter.WriteCompleted += VolumeWriteCompleted;
        _volumeWriter.WriteFailed += VolumeWriteFailed;
        _streamerWriter = new VolumeWriteCoordinator(WriteStreamerVolumeAsync);
        _streamerWriter.WriteCompleted += StreamerWriteCompleted;
        _streamerWriter.WriteFailed += StreamerWriteFailed;
        _sonarEventClient.VolumeChanged += SonarVolumeChanged;
        _sonarEventClient.ConnectionStateChanged += SonarEventConnectionChanged;

        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _syncTimer.Tick += (_, _) =>
        {
            if (IsVisible && !_isLoading && !_isWriting && !_isDragging && !_isDeviceDropDownOpen &&
                Mouse.LeftButton == MouseButtonState.Released)
            {
                LoadSonarStateAsync(showConnecting: false, includeRoutingData: true);
            }
        };
        _syncTimer.Start();

        _volumeFallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _volumeFallbackTimer.Tick += (_, _) =>
        {
            if (IsVisible && !_sonarEventClient.IsConnected && !_isLoading && !_isFallbackLoading &&
                !_isWriting && !_isDeviceDropDownOpen && Mouse.LeftButton == MouseButtonState.Released)
            {
                RefreshVolumesFallbackAsync();
            }
        };
        _volumeFallbackTimer.Start();
    }

    /// <summary>Shows a short message in the footer, used by tray commands.</summary>
    public void ReportStatus(string message)
    {
        ActionStatusText.Text = message;
    }

    public async void LoadSonarStateAsync(
        bool showConnecting = true,
        bool includeRoutingData = false,
        bool includeEqPresets = false)
    {
        if (_isLoading)
        {
            return;
        }

        _isLoading = true;
        if (showConnecting)
        {
            StatusText.Text = "正在连接 Sonar…";
            StatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(97, 105, 122));
        }

        try
        {
            SonarSnapshot snapshot;
            if (_sonarClient is not null)
            {
                try
                {
                    snapshot = await _sonarClient.GetSnapshotAsync(includeRoutingData, includeEqPresets);
                }
                catch
                {
                    _sonarClient.Dispose();
                    _sonarClient = null;
                    var recovered = await _stateService.LoadAsync(includeRoutingData, includeEqPresets);
                    _sonarClient = new SonarClient(recovered.Endpoint.BaseAddress);
                    StartEventClient(recovered.Endpoint.BaseAddress);
                    snapshot = recovered.Snapshot;
                }
            }
            else
            {
                var initial = await _stateService.LoadAsync(includeRoutingData, includeEqPresets);
                _sonarClient = new SonarClient(initial.Endpoint.BaseAddress);
                StartEventClient(initial.Endpoint.BaseAddress);
                snapshot = initial.Snapshot;
            }
            _endpointVolume.UpdateDevices(snapshot.Devices);
            _deviceCatalog = snapshot.Devices;
            var bindings = snapshot.Bindings.ToDictionary(
                binding => NormalizeBindingChannel(binding.Channel),
                binding => binding);
            var physicalDevices = snapshot.Devices
                .Where(device => device.State == "active" && !device.IsVirtual)
                .ToArray();
            var nextRows = snapshot.Volumes
                .OrderBy(volume => VolumeSortOrder(volume.Channel))
                .Select(volume =>
            {
                var binding = bindings.GetValueOrDefault(volume.Channel);
                var dataFlow = volume.Channel == "chatCapture" ? "capture" : "render";
                var options = volume.Channel == "master"
                    ? Array.Empty<DeviceOption>()
                    : physicalDevices
                        .Where(device => device.DataFlow == dataFlow)
                        .Select(device => new DeviceOption(device.FriendlyName, device.Id))
                        .ToArray();
                var eqOptions = snapshot.EqPresets
                    .Where(preset => preset.Channel == volume.Channel)
                    .OrderByDescending(preset => preset.IsFavorite)
                    .ThenBy(preset => preset.FavoritePosition)
                    .ThenBy(preset => preset.Name)
                    .Select(preset => new EqOption(preset.Name, preset.Id, preset.IsFavorite))
                    .ToList();
                var selectedEq = snapshot.SelectedEqPresets
                    .FirstOrDefault(preset => preset.Channel == volume.Channel);
                if (selectedEq is not null && !eqOptions.Any(option => option.Id == selectedEq.Id))
                {
                    eqOptions.Add(new EqOption(selectedEq.Name, selectedEq.Id, false));
                }
                return new ChannelRow(
                    volume.Channel,
                    ToBindingChannel(volume.Channel),
                    ChannelNames.GetValueOrDefault(volume.Channel, volume.Channel),
                    (int)Math.Round(volume.State.Volume * 100),
                    volume.State.Muted,
                    options,
                    binding?.DeviceId,
                    eqOptions,
                    selectedEq?.Id,
                    CreateRoleBrush(volume.Channel));
            }).ToArray();
            ApplyChannelRows(nextRows, snapshot.EqPresets.Count > 0);
            ApplyStreamerState(snapshot.Streamer, snapshot.SelectedEqPresets);

            if (includeRoutingData)
            {
                var routeDevices = snapshot.Devices
                    .Where(device => device.State == "active" && device.IsVirtual && device.DataFlow == "render" &&
                                     device.Role is "game" or "chatRender" or "media" or "aux")
                    .GroupBy(device => device.Role)
                    .Select(group => group.First())
                    .ToDictionary(device => device.Role, device => device);
                RoutingLaneList.ItemsSource = routeDevices.Values
                    .OrderBy(device => RouteSortOrder(device.Role))
                    .Select(device =>
                    {
                        var applications = snapshot.DeviceSessions
                            .Where(route => route.DataFlow == "render" && route.Role == device.Role)
                            .SelectMany(route => route.Sessions)
                            .Where(session => session.State == "active" && !session.IsSystemSound && session.ProcessId > 0)
                            .GroupBy(session => session.ProcessId)
                            .Select(group => new RoutedApplication(
                                group.First().DisplayName,
                                group.Key,
                                group.Count(),
                                device.Role))
                            .OrderBy(application => application.DisplayName)
                            .ToArray();
                        return new RoutingLane(
                            ChannelNames.GetValueOrDefault(device.Role, device.Role),
                            device.Role,
                            device.Id,
                            applications,
                            CreateRoleBrush(device.Role));
                    })
                    .ToArray();
            }
            StatusText.Text = snapshot.Mode == StreamMode
                ? $"Sonar 已连接 · 主播模式 · {snapshot.Devices.Count(device => device.State == "active")} 个活动设备"
                : $"Sonar 已连接 · 经典模式 · {snapshot.Devices.Count(device => device.State == "active")} 个活动设备";
            StatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(2, 221, 188));
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Sonar 不可用：{exception.Message}";
            StatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(255, 83, 89));
        }
        finally
        {
            _isLoading = false;
            if (includeRoutingData)
            {
                _hasLoadedHeavyData = true;
                _isDeferredLoading = false;
            }
            else if (!_hasLoadedHeavyData && !_isDeferredLoading)
            {
                _isDeferredLoading = true;
                _ = Dispatcher.BeginInvoke(
                    () => LoadSonarStateAsync(showConnecting: false, includeRoutingData: true),
                    DispatcherPriority.Background);
            }
        }
    }

    private static string NormalizeBindingChannel(string channel) => channel switch
    {
        "chat" => "chatRender",
        "mic" => "chatCapture",
        _ => channel
    };

    private static string? ToBindingChannel(string channel) => channel switch
    {
        "master" => null,
        "chatRender" => "chat",
        "chatCapture" => "mic",
        _ => channel
    };

    private static int RouteSortOrder(string role) => role switch
    {
        "game" => 0,
        "chatRender" => 1,
        "media" => 2,
        "aux" => 3,
        _ => 99
    };

    private static int VolumeSortOrder(string channel) => channel switch
    {
        "master" => 0,
        "game" => 1,
        "chatRender" => 2,
        "media" => 3,
        "aux" => 4,
        "chatCapture" => 5,
        _ => 99
    };

    private static MediaBrush CreateRoleBrush(string role) => role switch
    {
        "game" => new SolidColorBrush(MediaColor.FromRgb(0, 209, 169)),
        "chatRender" => new SolidColorBrush(MediaColor.FromRgb(46, 155, 245)),
        "media" => new SolidColorBrush(MediaColor.FromRgb(255, 59, 103)),
        "aux" => new SolidColorBrush(MediaColor.FromRgb(110, 75, 242)),
        _ => new SolidColorBrush(MediaColor.FromRgb(97, 105, 122))
    };

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!HideOnDeactivate)
        {
            return;
        }

        FinishTrackVolumeDrag();
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isDeviceDropDownOpen && !_isDragging && !IsKeyboardFocusWithin)
            {
                Hide();
            }
        }, DispatcherPriority.ContextIdle);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            _syncTimer.Stop();
            _volumeFallbackTimer.Stop();
            _sonarEventClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _volumeWriter.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _streamerWriter.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _sonarClient?.Dispose();
        }
    }

    private void VolumeSlider_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider { DataContext: ChannelRow row } slider)
        {
            return;
        }

        _volumeDragChannels.Add(row.Channel);
        if (string.Equals(row.Channel, "master", StringComparison.Ordinal))
        {
            CaptureClassicMasterBaseline(slider.Value);
        }

        BeginTrackDrag(slider, e, () => CompleteVolumeInteraction(slider, row));
    }

    private void CaptureStreamerMasterBaseline(StreamerChannelRow masterRow, double startPercent)
    {
        _masterDragStartPercent = startPercent;
        _masterDragBaseline.Clear();
        foreach (var sibling in _streamerRows.Values)
        {
            if (sibling is StreamerChannelRow { Channel: not "master" } channelRow &&
                string.Equals(channelRow.Mix, masterRow.Mix, StringComparison.Ordinal))
            {
                _masterDragBaseline[channelRow.Channel] = channelRow.Percent;
            }
        }
    }

    private void CaptureClassicMasterBaseline(double startPercent)
    {
        _classicMasterDragStartPercent = startPercent;
        _classicMasterBaseline.Clear();
        foreach (var sibling in _channelRows.Values)
        {
            if (!string.Equals(sibling.Channel, "master", StringComparison.Ordinal))
            {
                _classicMasterBaseline[sibling.Channel] = sibling.Percent;
            }
        }
    }

    private void StreamerSlider_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider { DataContext: StreamerVolumeRow row } slider ||
            string.IsNullOrEmpty(row.ActiveKey))
        {
            return;
        }

        var key = row.ActiveKey;
        if (row is StreamerChannelRow { Channel: "master" } masterRow)
        {
            CaptureStreamerMasterBaseline(masterRow, slider.Value);
        }

        _volumeDragChannels.Add(key);
        BeginTrackDrag(slider, e, () => CompleteStreamerInteraction(slider, key));
    }

    private void BeginTrackDrag(Slider slider, MouseButtonEventArgs e, Action complete)
    {
        if (e.OriginalSource is not DependencyObject source || IsThumbSource(source))
        {
            return;
        }

        if (slider.Template.FindName("PART_Track", slider) is not Track track ||
            !SetSliderValueFromPointer(slider, track, e))
        {
            return;
        }

        _trackDrag = new TrackDragSession(slider, track, complete);
        _trackDragHasWindowCapture = false;
        e.Handled = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (_trackDrag?.Slider != slider)
            {
                return;
            }

            if (Mouse.LeftButton != MouseButtonState.Pressed || !Mouse.Capture(this, CaptureMode.SubTree))
            {
                FinishTrackVolumeDrag();
                return;
            }

            _trackDragHasWindowCapture = Mouse.Captured == this;
            if (!_trackDragHasWindowCapture)
            {
                FinishTrackVolumeDrag();
            }
        }, DispatcherPriority.Input);
    }

    private void Window_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_trackDrag is not { } session)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishTrackVolumeDrag();
            return;
        }

        SetSliderValueFromPointer(session.Slider, session.Track, e);
        e.Handled = true;
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_trackDrag is not { } session)
        {
            return;
        }

        SetSliderValueFromPointer(session.Slider, session.Track, e);
        FinishTrackVolumeDrag();
        e.Handled = true;
    }

    private void Window_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isFinishingTrackDrag &&
            _trackDrag is not null &&
            _trackDragHasWindowCapture &&
            Mouse.Captured != this)
        {
            FinishTrackVolumeDrag();
        }
    }

    private void FinishTrackVolumeDrag()
    {
        if (_isFinishingTrackDrag || _trackDrag is not { } session)
        {
            return;
        }

        _isFinishingTrackDrag = true;
        _trackDrag = null;
        _trackDragHasWindowCapture = false;
        try
        {
            if (Mouse.Captured == this)
            {
                Mouse.Capture(null);
            }

            session.Complete();
        }
        finally
        {
            _isFinishingTrackDrag = false;
        }
    }

    private static bool SetSliderValueFromPointer(
        Slider slider,
        Track track,
        System.Windows.Input.MouseEventArgs e)
    {
        if (track.ActualWidth <= 0)
        {
            return false;
        }

        slider.Value = VolumeSliderMath.ValueFromTrackPosition(
            e.GetPosition(track).X,
            track.ActualWidth,
            track.Thumb?.ActualWidth ?? 0,
            slider.Minimum,
            slider.Maximum);
        return true;
    }

    private static bool IsThumbSource(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Thumb)
            {
                return true;
            }
        }

        return false;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isApplyingRemoteVolume || _isLoading ||
            sender is not Slider { DataContext: ChannelRow row } slider ||
            (!_volumeDragChannels.Contains(row.Channel) && !slider.IsKeyboardFocusWithin))
        {
            return;
        }

        if (string.Equals(row.Channel, "master", StringComparison.Ordinal))
        {
            if (_classicMasterDragStartPercent < 0d)
            {
                CaptureClassicMasterBaseline(e.OldValue);
            }

            ScaleClassicSiblings(e.NewValue);
        }
        else
        {
            RaiseClassicMaster(e.NewValue);
        }

        _volumeWriter.Queue(row.Channel, (float)Math.Clamp(e.NewValue / 100d, 0d, 1d), isFinal: false);
        ActionStatusText.Text = $"正在调整{row.DisplayName}音量…";
    }

    /// <summary>
    /// Classic master is a multiplier too: writing it rescales every other channel on the server.
    /// Mirror that immediately from the values captured when the drag started.
    /// </summary>
    private void ScaleClassicSiblings(double newPercent)
    {
        if (_classicMasterBaseline.Count == 0)
        {
            return;
        }

        foreach (var (channel, baseline) in _classicMasterBaseline)
        {
            if (!_channelRows.TryGetValue(channel, out var sibling) ||
                _volumeDragChannels.Contains(channel))
            {
                continue;
            }

            // Leaving 0 makes Sonar move every channel to the new master level.
            var target = _classicMasterDragStartPercent <= 0d
                ? newPercent
                : baseline * (newPercent / _classicMasterDragStartPercent);
            sibling.Percent = (int)Math.Round(target);
        }
    }

    /// <summary>
    /// Raising a channel above the master makes Sonar lift the master to that channel's level.
    /// </summary>
    private void RaiseClassicMaster(double newPercent)
    {
        if (_channelRows.TryGetValue("master", out var master) &&
            !_volumeDragChannels.Contains("master") &&
            newPercent > master.Percent)
        {
            master.Percent = (int)Math.Round(newPercent);
        }
    }

    private void VolumeSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { DataContext: ChannelRow row } slider)
        {
            Dispatcher.BeginInvoke(() => CompleteVolumeInteraction(slider, row), DispatcherPriority.Input);
        }
    }

    private void VolumeSlider_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Slider { DataContext: ChannelRow row } slider)
        {
            if (_trackDrag?.Slider == slider)
            {
                return;
            }

            CompleteVolumeInteraction(slider, row);
        }
    }

    private void VolumeSlider_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is Slider { DataContext: ChannelRow row } slider &&
            e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            _volumeWriter.Queue(row.Channel, (float)Math.Clamp(slider.Value / 100d, 0d, 1d), isFinal: true);
            _classicMasterBaseline.Clear();
            _classicMasterDragStartPercent = -1d;
        }
    }

    private void CompleteVolumeInteraction(Slider slider, ChannelRow row)
    {
        if (!_volumeDragChannels.Remove(row.Channel))
        {
            return;
        }

        _classicMasterBaseline.Clear();
        _classicMasterDragStartPercent = -1d;
        _volumeWriter.Queue(row.Channel, (float)Math.Clamp(slider.Value / 100d, 0d, 1d), isFinal: true);
    }

    private async Task<VolumeState> WriteVolumeAsync(
        string channel,
        float volume,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_endpointVolume.TrySetVolume(channel, volume, out var endpointState))
        {
            return endpointState;
        }

        var client = _sonarClient ?? throw new InvalidOperationException("Sonar is not connected.");
        await client.SetChannelVolumeAsync(channel, volume, cancellationToken);
        return await client.SetChannelMutedAsync(channel, volume <= 0f, cancellationToken);
    }

    private void VolumeWriteCompleted(string channel, VolumeState confirmed, bool isFinal)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_volumeDragChannels.Contains(channel))
            {
                ApplyVolumeUpdates([new ChannelVolumeUpdate(channel, confirmed.Volume, confirmed.Muted)]);
            }

            if (isFinal && _channelRows.TryGetValue(channel, out var row))
            {
                ActionStatusText.Text = string.Equals(row.DisplayName, "主音量", StringComparison.Ordinal)
                    ? "已更新主音量，通道音量已等比跟随"
                    : $"已更新{row.DisplayName}音量";
            }
        });
    }

    private void VolumeWriteFailed(string channel, Exception exception)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ActionStatusText.Text = $"音量同步失败：{exception.Message}";
            RefreshVolumesFallbackAsync();
        });
    }

    /// <summary>
    /// Sonar rejects streamer writes with "Cannot be called in current mode" while it is in classic
    /// mode, so make sure the mode matches before writing.
    /// </summary>
    private async Task EnsureStreamModeAsync(ISonarClient client, CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastModeCheck < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastModeCheck = DateTimeOffset.UtcNow;
        var mode = await client.GetModeAsync(cancellationToken).ConfigureAwait(true);
        if (!string.Equals(mode, StreamMode, StringComparison.Ordinal))
        {
            await client.SetModeAsync(StreamMode, cancellationToken).ConfigureAwait(true);
        }
    }

    private async Task<VolumeState> WriteStreamerVolumeAsync(
        string key,
        float volume,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = _sonarClient ?? throw new InvalidOperationException("Sonar is not connected.");
        var parts = key.Split('|');
        if (parts.Length == 3 && parts[0] == "stream")
        {
            await EnsureStreamModeAsync(client, cancellationToken).ConfigureAwait(true);
            return await client.SetStreamerVolumeAsync(parts[1], parts[2], volume, cancellationToken)
                .ConfigureAwait(true);
        }

        throw new ArgumentException($"Unknown Streamer volume target '{key}'.", nameof(key));
    }

    private void StreamerWriteCompleted(string key, VolumeState confirmed, bool isFinal)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var row = FindStreamerRow(key);
            if (row is not null && !_volumeDragChannels.Contains(key))
            {
                row.ApplyConfirmed(confirmed);
            }

            if (isFinal)
            {
                var mixLabel = _streamerMix == MonitoringMix ? "监听" : "直播";
                ActionStatusText.Text = row is null
                    ? $"已更新{mixLabel}混音"
                    : string.Equals(row.DisplayName, "主音量", StringComparison.Ordinal)
                        ? $"已更新{mixLabel}主音量，通道音量已等比跟随"
                        : $"已更新{mixLabel}·{row.DisplayName}音量（Sonar 已确认 {confirmed.Volume * 100:0}%）";
            }
        });
    }

    private void StreamerWriteFailed(string key, Exception exception)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ActionStatusText.Text = $"主播混音同步失败：{exception.Message}";
            LoadSonarStateAsync(showConnecting: false, includeRoutingData: true);
        });
    }

    private StreamerVolumeRow? FindStreamerRow(string key)
    {
        var separator = key.LastIndexOf('|');
        return separator < 0 ? null : _streamerRows.GetValueOrDefault(key[(separator + 1)..]);
    }

    private void CompleteStreamerInteraction(Slider slider, string key)
    {
        if (!_volumeDragChannels.Remove(key))
        {
            return;
        }

        _masterDragBaseline.Clear();
        _masterDragStartPercent = -1d;
        _streamerWriter.Queue(key, (float)Math.Clamp(slider.Value / 100d, 0d, 1d), isFinal: true);
    }

    private void StreamerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isApplyingRemoteVolume || _isLoading || _isApplyingStreamerState)
        {
            return;
        }

        if (sender is not Slider { DataContext: StreamerVolumeRow row } slider ||
            string.IsNullOrEmpty(row.ActiveKey) ||
            (!_volumeDragChannels.Contains(row.ActiveKey) && !slider.IsKeyboardFocusWithin))
        {
            return;
        }

        if (row is StreamerChannelRow { Channel: "master" } masterRow)
        {
            if (_masterDragStartPercent < 0d)
            {
                CaptureStreamerMasterBaseline(masterRow, e.OldValue);
            }

            ScaleStreamerSiblings(masterRow.Mix, e.NewValue);
        }
        else if (row is StreamerChannelRow channelRow)
        {
            RaiseStreamerMaster(channelRow.Mix, e.NewValue);
        }

        _streamerWriter.Queue(row.ActiveKey, (float)Math.Clamp(e.NewValue / 100d, 0d, 1d), isFinal: false);
        ActionStatusText.Text = $"正在调整{row.DisplayName}音量…";
    }

    /// <summary>
    /// Sonar's streamer master is a multiplier: writing it rescales every channel of that mix on
    /// the server (master 1.0 -> 0.5 turns game 0.8 into 0.4 and chat 0.6 into 0.3).  Mirror that
    /// on the other rows immediately, always computing from the values captured when the drag
    /// started so intermediate snapshots cannot compound the ratio.
    /// </summary>
    private void ScaleStreamerSiblings(string mix, double newPercent)
    {
        if (_masterDragBaseline.Count == 0)
        {
            return;
        }

        foreach (var sibling in _streamerRows.Values)
        {
            if (sibling is not StreamerChannelRow { Channel: not "master" } channelRow ||
                !string.Equals(channelRow.Mix, mix, StringComparison.Ordinal) ||
                !_masterDragBaseline.TryGetValue(channelRow.Channel, out var baseline) ||
                _volumeDragChannels.Contains($"stream|{mix}|{channelRow.Channel}"))
            {
                continue;
            }

            // Leaving 0 makes Sonar move every channel to the new master level.
            var target = _masterDragStartPercent <= 0d
                ? newPercent
                : baseline * (newPercent / _masterDragStartPercent);
            channelRow.PreviewMixVolume(mix, (int)Math.Round(target));
        }
    }

    /// <summary>
    /// Raising a channel above the master makes Sonar lift the master to that channel's level.
    /// </summary>
    private void RaiseStreamerMaster(string mix, double newPercent)
    {
        if (!_streamerRows.TryGetValue("master", out var master) ||
            master is not StreamerChannelRow masterRow ||
            !string.Equals(masterRow.Mix, mix, StringComparison.Ordinal) ||
            _volumeDragChannels.Contains("stream|master|master") ||
            newPercent <= masterRow.Percent)
        {
            return;
        }

        masterRow.Percent = (int)Math.Round(newPercent);
    }

    private void StreamerSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { DataContext: StreamerVolumeRow row } slider && !string.IsNullOrEmpty(row.ActiveKey))
        {
            var key = row.ActiveKey;
            Dispatcher.BeginInvoke(() => CompleteStreamerInteraction(slider, key), DispatcherPriority.Input);
        }
    }

    private void StreamerSlider_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Slider { DataContext: StreamerVolumeRow row } slider && !string.IsNullOrEmpty(row.ActiveKey))
        {
            if (_trackDrag?.Slider == slider)
            {
                return;
            }

            CompleteStreamerInteraction(slider, row.ActiveKey);
        }
    }

    private void StreamerSlider_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is Slider { DataContext: StreamerVolumeRow row } slider &&
            !string.IsNullOrEmpty(row.ActiveKey) &&
            e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            _streamerWriter.Queue(row.ActiveKey, (float)Math.Clamp(slider.Value / 100d, 0d, 1d), isFinal: true);
            _masterDragBaseline.Clear();
            _masterDragStartPercent = -1d;
        }
    }

    private async void StreamerMuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: StreamerVolumeRow row } || _sonarClient is null)
        {
            return;
        }

        var client = _sonarClient;
        var targetMuted = !row.Muted;
        var volume = targetMuted ? 0f : row.LastNonZeroPercent / 100f;
        await ExecuteWriteAsync(async () =>
        {
            VolumeState confirmed;
            switch (row)
            {
                case StreamerChannelRow channelRow:
                    await client.SetStreamerVolumeAsync(channelRow.Mix, channelRow.Channel, volume);
                    confirmed = await client.SetStreamerMutedAsync(channelRow.Mix, channelRow.Channel, volume <= 0f);
                    break;
                default:
                    return;
            }

            row.ApplyConfirmed(confirmed);
        }, targetMuted ? $"已静音{row.DisplayName}" : $"已恢复{row.DisplayName}音量");    }

    private void ApplyModeLayout()
    {
        var streamerVisible = _currentMode == StreamMode && _hasStreamerData;
        StreamerPanel.Visibility = streamerVisible ? Visibility.Visible : Visibility.Collapsed;
        ClassicPanel.Visibility = streamerVisible ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton { Tag: string mode } || _sonarClient is null || _isApplyingStreamerState ||
            string.Equals(mode, _currentMode, StringComparison.Ordinal))
        {
            return;
        }

        var client = _sonarClient;
        _currentMode = mode;
        _modeSwitchGuardUntil = DateTimeOffset.UtcNow.AddSeconds(3);
        _lastModeCheck = DateTimeOffset.MinValue;
        ApplyModeLayout();
        await ExecuteWriteAsync(async () =>
        {
            // Sonar's /mode switch is what GG's own "Streamer mode" toggle writes, so there is no
            // need to touch the GG window at all: GG picks the new mode up again as soon as it is
            // restored, and a minimized or tray-only GG switches exactly the same way.
            await client.SetModeAsync(mode);
            // GG rebuilds every Sonar endpoint after a mode switch; wait briefly so the follow-up
            // snapshot does not read the previous mode back.
            await Task.Delay(400);
        }, mode == StreamMode ? "已切换到主播模式" : "已切换到经典模式");
    }

    private void ApplyMixSelection()
    {
        // The monitoring switch doubles as the mix selector, following GG's own wiring:
        // monitoring off edits the personal mixer (monitoring), monitoring on edits the
        // streaming processor (streaming).
        var mix = StreamMonitoringToggle.IsChecked == true ? StreamingMix : MonitoringMix;
        var changed = !string.Equals(_streamerMix, mix, StringComparison.Ordinal);
        _streamerMix = mix;
        foreach (var row in _streamerRows.Values)
        {
            row.ShowMix(mix, updateDisplayedVolume: !_volumeDragChannels.Contains($"stream|{mix}|{row.Channel}"));
        }

        ApplyStreamSources(_lastStreamer);
        if (changed && !_isApplyingStreamerState)
        {
            ActionStatusText.Text = mix == StreamingMix ? "正在调整直播混音" : "正在调整监听混音";
        }
    }

    private async void StreamMonitoring_Click(object sender, RoutedEventArgs e)
    {
        if (_sonarClient is null || _isApplyingStreamerState)
        {
            return;
        }

        var client = _sonarClient;
        var enabled = StreamMonitoringToggle.IsChecked == true;
        _monitoringGuardUntil = DateTimeOffset.UtcNow.AddSeconds(4);
        // Bind the rows to the new mix first: the switch is the user's intent, so the very next
        // drag must hit the mix they just selected even if the Sonar call below fails.
        ApplyMixSelection();
        await ExecuteWriteAsync(async () =>
        {
            await EnsureStreamModeAsync(client, CancellationToken.None);
            var confirmed = await client.SetStreamMonitoringEnabledAsync(enabled);
            _isApplyingStreamerState = true;
            try
            {
                StreamMonitoringToggle.IsChecked = confirmed;
            }
            finally
            {
                _isApplyingStreamerState = false;
            }

            ApplyMixSelection();
        }, enabled
            ? "已开启监听，现在调整的是流式处理器（直播）音量"
            : "已关闭监听，现在调整的是个人合成器（监听）音量",
            onFailure: () =>
            {
                _monitoringGuardUntil = DateTimeOffset.MinValue;
                _isApplyingStreamerState = true;
                try
                {
                    StreamMonitoringToggle.IsChecked = !enabled;
                }
                finally
                {
                    _isApplyingStreamerState = false;
                }

                ApplyMixSelection();
            });
    }

    private void ApplyStreamerEq(IReadOnlyList<SelectedEqPreset> selected)
    {
        _isApplyingStreamerEq = true;
        try
        {
            foreach (var row in _streamerRows.Values)
            {
                if (row.EqChannel is null)
                {
                    continue;
                }

                var preset = selected.FirstOrDefault(entry =>
                    string.Equals(entry.Channel, row.EqChannel, StringComparison.Ordinal));
                row.SetSelectedEq(preset is null ? null : new EqOption(preset.Name, preset.Id, IsFavorite: true));
                if (row.EqOptions.Count == 0)
                {
                    _ = LoadStreamerEqOptionsAsync(row);
                }
            }
        }
        finally
        {
            _isApplyingStreamerEq = false;
        }
    }

    private async void StreamerEq_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox comboBox ||
            comboBox.DataContext is not StreamerVolumeRow row)
        {
            return;
        }

        await LoadStreamerEqOptionsAsync(row);
        // Replacing ItemsSource makes the ComboBox scroll the selected preset into view, which
        // reads as the list jumping by itself.  Put the list back to the top (favourites first).
        _ = Dispatcher.BeginInvoke(() => ScrollEqListToTop(comboBox), DispatcherPriority.Loaded);
    }

    private async Task LoadStreamerEqOptionsAsync(StreamerVolumeRow row)
    {
        if (_sonarClient is null || row.EqChannel is not { } channel || !_loadingStreamerEq.Add(channel))
        {
            return;
        }

        try
        {
            var presets = await _sonarClient.GetEqPresetsAsync(channel);
            var options = presets
                .OrderByDescending(preset => preset.IsFavorite)
                .ThenBy(preset => preset.FavoritePosition)
                .ThenBy(preset => preset.Name)
                .Select(preset => new EqOption(preset.Name, preset.Id, preset.IsFavorite))
                .ToArray();
            _isApplyingStreamerEq = true;
            try
            {
                row.SetEqOptions(options);
            }
            finally
            {
                _isApplyingStreamerEq = false;
            }
        }
        catch (Exception exception)
        {
            ActionStatusText.Text = $"EQ 加载失败：{exception.Message}";
        }
        finally
        {
            _loadingStreamerEq.Remove(channel);
        }
    }

    private async void StreamerEq_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingStreamerEq || _isApplyingStreamerState || _sonarClient is null ||
            sender is not System.Windows.Controls.ComboBox { DataContext: StreamerVolumeRow row } combo ||
            combo.SelectedItem is not EqOption option ||
            string.IsNullOrEmpty(option.Id))
        {
            return;
        }

        var client = _sonarClient;
        await ExecuteWriteAsync(async () =>
        {
            await client.SelectEqPresetAsync(option.Id);
            _isApplyingStreamerEq = true;
            try
            {
                row.SetSelectedEq(option);
            }
            finally
            {
                _isApplyingStreamerEq = false;
            }
        }, $"{row.DisplayName}音效已切换到 {option.Name}");
    }

    private async void StreamSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: StreamSourceChip chip } toggle || _sonarClient is null)
        {
            return;
        }

        var client = _sonarClient;
        var enabled = toggle.IsChecked == true;
        await ExecuteWriteAsync(async () =>
        {
            var confirmed = await client.SetStreamSourceEnabledAsync(chip.Mix, chip.Role, enabled);
            chip.IsEnabled = confirmed.IsEnabled;
        }, $"{chip.Label}已{(enabled ? "加入" : "移出")}{(chip.Mix == StreamingMix ? "直播" : "监听")}混音");
    }

    private void SonarVolumeChanged(IReadOnlyList<ChannelVolumeUpdate> updates)
    {
        Dispatcher.BeginInvoke(() => ApplyVolumeUpdates(updates), DispatcherPriority.DataBind);
    }

    private void SonarEventConnectionChanged(bool connected)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ActionStatusText.Text = connected
                ? "Sonar 实时同步已连接"
                : "实时同步已断开，正在使用轮询并尝试重连";
            if (connected)
            {
                RefreshVolumesFallbackAsync();
            }
        });
    }

    private void StartEventClient(Uri baseAddress)
    {
        if (_eventBaseAddress == baseAddress)
        {
            return;
        }

        _eventBaseAddress = baseAddress;
        _sonarEventClient.Start(baseAddress);
    }

    private async void RefreshVolumesFallbackAsync()
    {
        if (_isFallbackLoading || _sonarClient is null)
        {
            return;
        }

        _isFallbackLoading = true;
        try
        {
            var volumes = await _sonarClient.GetClassicVolumesAsync();
            ApplyVolumeUpdates(volumes.Select(volume =>
                new ChannelVolumeUpdate(volume.Channel, volume.State.Volume, volume.State.Muted)).ToArray());
        }
        catch
        {
            if (!_isLoading)
            {
                LoadSonarStateAsync(showConnecting: false);
            }
        }
        finally
        {
            _isFallbackLoading = false;
        }
    }

    private void ApplyChannelRows(IReadOnlyList<ChannelRow> nextRows, bool includesCompleteEqCatalog)
    {
        var topologyChanged = _channelRows.Count != nextRows.Count ||
                              nextRows.Any(row => !_channelRows.ContainsKey(row.Channel));
        if (topologyChanged)
        {
            _channelRows.Clear();
            _loadedEqChannels.Clear();
            _loadingEqChannels.Clear();
            foreach (var row in nextRows)
            {
                _channelRows.Add(row.Channel, row);
            }

            ChannelList.ItemsSource = nextRows;
            return;
        }

        _isApplyingRemoteVolume = true;
        try
        {
            foreach (var next in nextRows)
            {
                var current = _channelRows[next.Channel];
                current.ApplyExternalState(
                    next.SnapshotPercent,
                    next.Muted,
                    updateDisplayedVolume: !_volumeDragChannels.Contains(next.Channel));
                current.UpdateDeviceOptions(next.DeviceOptions, next.BoundDeviceId);
                if (includesCompleteEqCatalog)
                {
                    current.UpdateEqOptions(next.AllEqOptions, next.SelectedEq?.Id);
                    _loadedEqChannels.Add(next.Channel);
                }
                else if (current.UpdateSelectedEq(next.SelectedEq, _loadedEqChannels.Contains(next.Channel)))
                {
                    // The selected EQ is not part of our cached catalogue (for example it was
                    // created in GG). Reload this channel the next time its menu is opened.
                    _loadedEqChannels.Remove(next.Channel);
                }
            }
        }
        finally
        {
            _isApplyingRemoteVolume = false;
        }
    }

    private void ApplyVolumeUpdates(IReadOnlyList<ChannelVolumeUpdate> updates)
    {
        _isApplyingRemoteVolume = true;
        try
        {
            foreach (var update in updates)
            {
                if (!_channelRows.TryGetValue(update.Channel, out var row))
                {
                    continue;
                }

                var masterHold = _classicMasterDragStartPercent > 0d &&
                                 !string.Equals(update.Channel, "master", StringComparison.Ordinal);
                var canUpdateDisplayedVolume = !masterHold && !_volumeDragChannels.Contains(update.Channel);
                int? percent = update.Volume.HasValue
                    ? (int)Math.Round(Math.Clamp(update.Volume.Value, 0f, 1f) * 100)
                    : null;

                if (percent is > 0)
                {
                    row.RememberNonZero(percent.Value);
                }

                if (update.Muted.HasValue)
                {
                    row.Muted = update.Muted.Value;
                    if (canUpdateDisplayedVolume)
                    {
                        row.Percent = update.Muted.Value
                            ? 0
                            : percent is > 0 ? percent.Value : row.LastNonZeroPercent;
                    }
                }
                else if (percent.HasValue)
                {
                    row.Muted = percent.Value == 0;
                    if (canUpdateDisplayedVolume)
                    {
                        row.Percent = percent.Value;
                    }
                }
            }
        }
        finally
        {
            _isApplyingRemoteVolume = false;
        }
    }

    private void ApplyStreamerState(StreamerSnapshot? streamer, IReadOnlyList<SelectedEqPreset>? selectedEqPresets = null)
    {
        _lastStreamer = streamer;
        _hasStreamerData = streamer is not null;
        _isApplyingStreamerState = true;
        try
        {
            if (streamer is not null && DateTimeOffset.UtcNow >= _modeSwitchGuardUntil)
            {
                // A snapshot taken before the user's own mode switch would otherwise flip the
                // panel back and make the next drag adjust the wrong mix.
                _currentMode = streamer.Mode;
            }

            ClassicModeButton.IsChecked = _currentMode != StreamMode;
            StreamModeButton.IsChecked = _currentMode == StreamMode;
            ApplyModeLayout();

            StreamMonitoringToggle.IsChecked = streamer?.IsMonitoringEnabled ?? false;
            StreamMonitoringToggle.IsEnabled = streamer is not null && !streamer.IsMonitoringLocked;
            if (DateTimeOffset.UtcNow < _monitoringGuardUntil && streamer is not null)
            {
                // A snapshot captured before the user's toggle would otherwise flip the switch back
                // and send the next drag to the other mix. Keep the user's intent for a moment.
                StreamMonitoringToggle.IsChecked = _streamerMix == StreamingMix;
            }
            StreamMonitoringHint.Text = streamer is null
                ? "当前 Sonar 未提供直播监听状态。"
                : streamer.IsMonitoringLocked
                    ? "直播监听已被 GG 锁定，当前调整的是流式处理器（直播）音量。"
                    : "关闭监听时调整个人合成器（监听）音量；开启后调整流式处理器（直播）音量。";
            StreamMonitoringHint.Visibility = streamer is null || streamer.IsMonitoringLocked
                ? Visibility.Visible
                : Visibility.Collapsed;

            ApplyMixSelection();
            ApplyStreamerEq(selectedEqPresets ?? []);
            StreamerEmptyText.Visibility = streamer is null ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _isApplyingStreamerState = false;
        }

        ApplyStreamerVolumes(streamer);
        ApplyStreamSources(streamer);
        ApplyStreamDevices(streamer);
    }

    private void ApplyStreamerVolumes(StreamerSnapshot? streamer)
    {
        if (streamer is null)
        {
            _streamerRows.Clear();
            StreamerChannelList.ItemsSource = null;
            return;
        }

        var channels = streamer.Volumes
            .Select(volume => volume.Channel)
            .Distinct()
            .OrderBy(VolumeSortOrder)
            .ToArray();
        var needsRebuild = _streamerRows.Count != channels.Length ||
                           channels.Any(channel => !_streamerRows.ContainsKey(channel));
        if (needsRebuild)
        {
            _streamerRows.Clear();
            foreach (var channel in channels)
            {
                _streamerRows.Add(channel, new StreamerChannelRow(
                    channel,
                    ChannelNames.GetValueOrDefault(channel, channel),
                    CreateRoleBrush(channel)));
            }

            StreamerChannelList.ItemsSource = channels.Select(channel => _streamerRows[channel]).ToArray();
        }

        foreach (var channel in channels)
        {
            var row = _streamerRows[channel];
            foreach (var volume in streamer.Volumes)
            {
                if (string.Equals(volume.Channel, channel, StringComparison.Ordinal))
                {
                    row.UpdateMix(volume.Mix, volume.State);
                }
            }

            var masterHold = _masterDragStartPercent > 0d && !string.Equals(channel, "master", StringComparison.Ordinal);
            row.ShowMix(
                _streamerMix,
                updateDisplayedVolume: !masterHold && !_volumeDragChannels.Contains($"stream|{_streamerMix}|{channel}"));
        }
    }

    private void ApplyStreamSources(StreamerSnapshot? streamer)
    {
        if (streamer is null)
        {
            _streamSources = [];
            StreamSourceList.ItemsSource = null;
            return;
        }

        var sources = streamer.Sources
            .Where(source => string.Equals(source.Mix, _streamerMix, StringComparison.Ordinal))
            .OrderBy(source => RouteSortOrder(source.Role))
            .ToArray();
        var needsRebuild = _streamSources.Count != sources.Length ||
                           _streamSources.Any(chip => !string.Equals(chip.Mix, _streamerMix, StringComparison.Ordinal)) ||
                           !_streamSources.Select(chip => chip.Role)
                               .SequenceEqual(sources.Select(source => source.Role), StringComparer.Ordinal);
        if (needsRebuild)
        {
            _streamSources = sources
                .Select(source => new StreamSourceChip(
                    _streamerMix,
                    source.Role,
                    ChannelNames.GetValueOrDefault(source.Role, source.Role),
                    source.IsEnabled))
                .ToArray();
            StreamSourceList.ItemsSource = _streamSources;
        }
        else
        {
            foreach (var source in sources)
            {
                var chip = _streamSources.FirstOrDefault(
                    entry => string.Equals(entry.Role, source.Role, StringComparison.Ordinal));
                if (chip is not null)
                {
                    chip.IsEnabled = source.IsEnabled;
                }
            }
        }
    }

    private void ApplyStreamDevices(StreamerSnapshot? streamer)
    {
        var previousGuard = _isApplyingStreamerState;
        _isApplyingStreamerState = true;
        try
        {
            if (streamer is null)
            {
                _monitoringDeviceId = string.Empty;
                _micDeviceId = string.Empty;
                MonitoringDeviceCombo.ItemsSource = null;
                MicDeviceCombo.ItemsSource = null;
                MonitoringDeviceCombo.IsEnabled = false;
                MicDeviceCombo.IsEnabled = false;
                return;
            }

            _monitoringDeviceId = FindStreamOutput(streamer, MonitoringMix);
            _micDeviceId = FindStreamOutput(streamer, MicMix);
            PopulateStreamDeviceCombo(MonitoringDeviceCombo, "render", _monitoringDeviceId);
            PopulateStreamDeviceCombo(MicDeviceCombo, "capture", _micDeviceId);
        }
        finally
        {
            _isApplyingStreamerState = previousGuard;
        }
    }

    private static string FindStreamOutput(StreamerSnapshot streamer, string mix) =>
        streamer.Outputs
            .FirstOrDefault(entry => string.Equals(entry.Mix, mix, StringComparison.Ordinal))
            ?.DeviceId ?? string.Empty;

    private static bool IsPhysicalDevice(AudioDevice device) =>
        !device.IsVirtual &&
        !device.FriendlyName.Contains("SteelSeries Sonar", StringComparison.OrdinalIgnoreCase);

    private void PopulateStreamDeviceCombo(System.Windows.Controls.ComboBox comboBox, string dataFlow, string deviceId)
    {
        var devices = _deviceCatalog
            .Where(device => string.Equals(device.DataFlow, dataFlow, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var physical = devices.Where(IsPhysicalDevice).ToArray();
        // 优先只列物理设备；若一条都没有（例如机器上只有虚拟声卡），退回完整列表，避免无法选择。
        var pool = physical.Length > 0 ? physical : devices;
        var options = pool
            .OrderBy(device => device.FriendlyName, StringComparer.CurrentCulture)
            .Select(device => new DeviceOption(device.FriendlyName, device.Id))
            .ToList();
        var bound = devices.FirstOrDefault(
            device => string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        var selected = options.FirstOrDefault(
            option => string.Equals(option.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        if (selected is null && bound is not null)
        {
            // 当前绑定的仍是虚拟设备时，保留它作为选中项，但不出现在可选列表里。
            selected = new DeviceOption(bound.FriendlyName, bound.Id);
            options.Insert(0, selected);
        }

        comboBox.ItemsSource = options;
        comboBox.SelectedItem = selected;
        comboBox.ToolTip = selected?.Name ?? (string.IsNullOrEmpty(deviceId) ? null : deviceId);
        comboBox.IsEnabled = options.Count > 0;
    }

    private async void StreamDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingStreamerState ||
            _sonarClient is null ||
            sender is not System.Windows.Controls.ComboBox { SelectedItem: DeviceOption selected } comboBox)
        {
            return;
        }

        var mix = ReferenceEquals(comboBox, MicDeviceCombo) ? MicMix : MonitoringMix;
        var current = mix == MicMix ? _micDeviceId : _monitoringDeviceId;
        if (string.Equals(selected.Id, current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await ExecuteWriteAsync(async () =>
        {
            var confirmed = await _sonarClient.SetStreamerOutputDeviceAsync(mix, selected.Id);
            if (mix == MicMix)
            {
                _micDeviceId = confirmed.DeviceId;
            }
            else
            {
                _monitoringDeviceId = confirmed.DeviceId;
            }
        }, $"{StreamDeviceLabel(mix)}已切换到 {selected.Name}");
    }

    private void StreamDevice_DropDownOpened(object sender, EventArgs e)
    {
        _isDeviceDropDownOpen = true;
    }

    private void StreamDevice_DropDownClosed(object sender, EventArgs e)
    {
        _isDeviceDropDownOpen = false;
    }

    private static string StreamDeviceLabel(string mix) => mix == MicMix ? "麦克风输入" : "个人合成器";

    private string DeviceDisplayName(string deviceId)
    {
        var device = _deviceCatalog.FirstOrDefault(
            entry => string.Equals(entry.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        return device is null ? deviceId : device.FriendlyName;
    }

    private async void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: ChannelRow row } || _sonarClient is null)
        {
            return;
        }

        var targetMuted = !row.Muted;
        var targetPercent = targetMuted ? 0 : row.LastNonZeroPercent;
        await ExecuteWriteAsync(async () =>
        {
            var confirmed = await WriteVolumeAsync(
                row.Channel,
                targetPercent / 100f,
                CancellationToken.None);
            row.Percent = confirmed.Muted ? 0 : (int)Math.Round(confirmed.Volume * 100);
            row.Muted = confirmed.Muted;
        }, targetMuted ? $"已静音{row.DisplayName}" : $"已恢复{row.DisplayName}音量");
    }

    private async void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox { DataContext: ChannelRow row, SelectedItem: DeviceOption selected } ||
            _sonarClient is null || row.BindingChannel is null || selected.Id == row.BoundDeviceId)
        {
            return;
        }

        await ExecuteWriteAsync(async () =>
        {
            var confirmed = await _sonarClient.SetChannelBindingAsync(row.BindingChannel, selected.Id);
            row.UpdateDeviceOptions(row.DeviceOptions, confirmed.DeviceId);
        }, $"{row.DisplayName}已切换到 {selected.Name}");
    }

    private void DeviceComboBox_DropDownOpened(object sender, EventArgs e)
    {
        _isDeviceDropDownOpen = true;
    }

    private void DeviceComboBox_DropDownClosed(object sender, EventArgs e)
    {
        _isDeviceDropDownOpen = false;
    }

    private async void EqComboBox_DropDownOpened(object sender, EventArgs e)
    {
        _isDeviceDropDownOpen = true;
        if (sender is not System.Windows.Controls.ComboBox comboBox || comboBox.DataContext is not ChannelRow row)
        {
            return;
        }

        await LoadEqOptionsAsync(row);
        // Replacing ItemsSource makes the ComboBox scroll the selected preset into view, which
        // reads as the list jumping by itself.  Put the list back to the top (favourites first).
        _ = Dispatcher.BeginInvoke(() => ScrollEqListToTop(comboBox), DispatcherPriority.Loaded);
    }

    private void EqComboBox_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Warm the preset cache before the popup opens, so the list is not rebuilt while the
        // user can already see it (that rebuild is what read as an automatic page jump).
        if (sender is System.Windows.Controls.ComboBox { DataContext: ChannelRow row } &&
            row.EqMenuOptions.Count == 0)
        {
            _ = LoadEqOptionsAsync(row);
        }
    }

    private void StreamerEq_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox { DataContext: StreamerVolumeRow row } &&
            row.EqOptions.Count == 0)
        {
            _ = LoadStreamerEqOptionsAsync(row);
        }
    }

    private async Task LoadEqOptionsAsync(ChannelRow row)
    {
        if (_sonarClient is null || !_loadingEqChannels.Add(row.Channel))
        {
            return;
        }

        var hadCachedOptions = _loadedEqChannels.Contains(row.Channel);
        if (!hadCachedOptions)
        {
            row.SetEqLoading(true);
        }

        try
        {
            // Revalidate a channel when its menu is opened. GG does not provide a dependable
            // preset-catalogue-changed event, so retaining the old list forever would hide
            // presets added, deleted, or re-favorited in GG.
            var presets = await _sonarClient.GetEqPresetsAsync(row.Channel, forceRefresh: hadCachedOptions);
            var options = presets
                .OrderByDescending(preset => preset.IsFavorite)
                .ThenBy(preset => preset.FavoritePosition)
                .ThenBy(preset => preset.Name)
                .Select(preset => new EqOption(preset.Name, preset.Id, preset.IsFavorite))
                .ToArray();
            row.UpdateEqOptions(options, row.SelectedEq?.Id);
            _loadedEqChannels.Add(row.Channel);
        }
        catch (Exception exception)
        {
            ActionStatusText.Text = $"EQ 加载失败：{exception.Message}";
        }
        finally
        {
            if (!hadCachedOptions)
            {
                row.SetEqLoading(false);
            }

            _loadingEqChannels.Remove(row.Channel);
        }
    }

    private static void ScrollEqListToTop(System.Windows.Controls.ComboBox comboBox)
    {
        if (comboBox.Template.FindName("PART_Popup", comboBox) is not Popup { Child: DependencyObject popupChild } ||
            FindVisualDescendant<ScrollViewer>(popupChild) is not { } scrollViewer)
        {
            return;
        }

        scrollViewer.ScrollToTop();
    }

    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found)
        {
            return found;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var descendant = FindVisualDescendant<T>(VisualTreeHelper.GetChild(root, index));
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private async void EqComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox { DataContext: ChannelRow row, SelectedItem: EqOption selected } ||
            _sonarClient is null || selected.IsLoading || selected.Id == row.SelectedEq?.Id)
        {
            return;
        }

        await ExecuteWriteAsync(async () =>
        {
            await _sonarClient.SelectEqPresetAsync(selected.Id);
            row.UpdateEqOptions(row.AllEqOptions, selected.Id);
            // GG paints the selected preset from its own cache and never re-reads it after an
            // external switch; only a GG restart makes its dropdown show the new preset.
        }, $"{row.DisplayName}已切换到 {selected.Name}；GG 页面需重启 GG 后才会刷新");
    }

    private void RoutedApplication_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(this);
    }

    private void RoutedApplication_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            sender is not FrameworkElement { DataContext: RoutedApplication application } element)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _isDragging = true;
        _draggedElement = element;
        ShowDragFeedback(application, element);
        try
        {
            var data = new System.Windows.DataObject(typeof(RoutedApplication), application);
            System.Windows.DragDrop.DoDragDrop(element, data, System.Windows.DragDropEffects.Move);
        }
        finally
        {
            HideDragFeedback();
            _isDragging = false;
        }
    }

    private void ShowDragFeedback(RoutedApplication application, FrameworkElement source)
    {
        DragGhostName.Text = application.DisplayName;
        DragGhostRoute.Text = $"来自 {ChannelNames.GetValueOrDefault(application.SourceRole, application.SourceRole)}";
        DragGhost.Visibility = Visibility.Visible;
        DragGhost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.96, TimeSpan.FromMilliseconds(130)));
        DragGhostScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(130)));
        DragGhostScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(130)));
        source.BeginAnimation(OpacityProperty, new DoubleAnimation(source.Opacity, 0.3, TimeSpan.FromMilliseconds(100)));
        UpdateDragGhostPosition(Mouse.GetPosition(LayoutRoot));
    }

    private void HideDragFeedback()
    {
        ResetDropTarget();
        if (_draggedElement is not null)
        {
            _draggedElement.BeginAnimation(OpacityProperty, null);
            _draggedElement.Opacity = 1;
            _draggedElement = null;
        }

        DragGhost.BeginAnimation(OpacityProperty, null);
        DragGhost.Visibility = Visibility.Collapsed;
    }

    private void Window_PreviewDragOver(object sender, System.Windows.DragEventArgs e) =>
        UpdateDragGhostPosition(e.GetPosition(LayoutRoot));

    private void UpdateDragGhostPosition(WpfPoint position)
    {
        Canvas.SetLeft(DragGhost, Math.Min(position.X + 15, Math.Max(0, LayoutRoot.ActualWidth - DragGhost.ActualWidth)));
        Canvas.SetTop(DragGhost, Math.Min(position.Y + 15, Math.Max(0, LayoutRoot.ActualHeight - DragGhost.ActualHeight)));
    }

    private void RoutingLane_DragEnter(object sender, System.Windows.DragEventArgs e) =>
        UpdateDropTarget(sender, e);

    private void RoutingLane_DragOver(object sender, System.Windows.DragEventArgs e) =>
        UpdateDropTarget(sender, e);

    private void RoutingLane_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is Border border && ReferenceEquals(border, _dropTargetBorder))
        {
            var position = e.GetPosition(border);
            if (position.X < 0 || position.Y < 0 || position.X > border.ActualWidth || position.Y > border.ActualHeight)
            {
                ResetDropTarget();
            }
        }
    }

    private void UpdateDropTarget(object sender, System.Windows.DragEventArgs e)
    {
        var application = e.Data.GetData(typeof(RoutedApplication)) as RoutedApplication;
        var lane = (sender as FrameworkElement)?.DataContext as RoutingLane;
        var canDrop = application is not null && lane is not null && application.SourceRole != lane.Role;
        e.Effects = canDrop ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
        e.Handled = true;
        if (sender is Border border && lane is not null)
        {
            if (canDrop)
            {
                SetDropTarget(border, lane);
                DragGhostRoute.Text = $"移动至 {lane.Name}";
            }
            else
            {
                ResetDropTarget();
                DragGhostRoute.Text = $"已在 {lane.Name}";
            }
        }
    }

    private void SetDropTarget(Border border, RoutingLane lane)
    {
        if (ReferenceEquals(_dropTargetBorder, border))
        {
            return;
        }

        ResetDropTarget();
        _dropTargetBorder = border;
        _dropTargetLane = lane;
        lane.ShowDropHint();
        border.BorderBrush = lane.AccentBrush;
        border.BeginAnimation(Border.OpacityProperty,
            new DoubleAnimation(0.78, 1, TimeSpan.FromMilliseconds(180)) { AutoReverse = true });
    }

    private void ResetDropTarget()
    {
        if (_dropTargetBorder is not null)
        {
            _dropTargetBorder.BeginAnimation(Border.OpacityProperty, null);
            _dropTargetBorder.Opacity = 1;
            _dropTargetBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(51, 65, 137));
        }

        _dropTargetLane?.HideDropHint();
        _dropTargetBorder = null;
        _dropTargetLane = null;
    }

    private async void RoutingLane_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is not Border { DataContext: RoutingLane lane } border ||
            e.Data.GetData(typeof(RoutedApplication)) is not RoutedApplication application ||
            application.SourceRole == lane.Role || _sonarClient is null)
        {
            return;
        }

        ResetDropTarget();
        await ExecuteWriteAsync(async () =>
        {
            await _sonarClient.RouteApplicationAsync("render", lane.DeviceId, application.ProcessId);
            await Task.Delay(250);
        }, $"{application.DisplayName}的默认输出已设为 {lane.Name}");
        // Route cards are sourced from /AudioDeviceRouting, which is intentionally omitted
        // from normal lightweight refreshes. Refresh it immediately after a successful drop
        // instead of waiting for the five-second background polling interval.
        LoadSonarStateAsync(showConnecting: false, includeRoutingData: true);
    }

    private async Task ExecuteWriteAsync(Func<Task> operation, string successMessage, Action? onFailure = null)
    {
        _isWriting = true;
        ActionStatusText.Text = "正在应用…";
        try
        {
            await operation();
            ActionStatusText.Text = successMessage;
        }
        catch (Exception exception)
        {
            ActionStatusText.Text = $"操作失败：{exception.Message}";
            onFailure?.Invoke();
            LoadSonarStateAsync();
        }
        finally
        {
            _isWriting = false;
        }
    }

    private sealed class ChannelRow : INotifyPropertyChanged
    {
        private int _percent;
        private bool _muted;
        private bool _isEqLoading;
        private int _lastNonZeroPercent;
        private string _eqSearchText = string.Empty;

        public ChannelRow(
            string channel,
            string? bindingChannel,
            string displayName,
            int percent,
            bool muted,
            IReadOnlyList<DeviceOption> deviceOptions,
            string? boundDeviceId,
            IReadOnlyList<EqOption> eqOptions,
            string? selectedEqId,
            MediaBrush accentBrush)
        {
            Channel = channel;
            BindingChannel = bindingChannel;
            DisplayName = displayName;
            _lastNonZeroPercent = percent > 0 ? percent : 50;
            _muted = muted || percent == 0;
            _percent = _muted ? 0 : percent;
            SnapshotPercent = percent;
            DeviceOptions = deviceOptions;
            BoundDeviceId = boundDeviceId;
            SelectedDevice = deviceOptions.FirstOrDefault(device => device.Id == boundDeviceId);
            AllEqOptions = eqOptions;
            SelectedEq = eqOptions.FirstOrDefault(option => option.Id == selectedEqId);
            EqMenuOptions = BuildEqMenuOptions();
            AccentBrush = accentBrush;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Channel { get; }

        public string? BindingChannel { get; }

        public string DisplayName { get; }

        public IReadOnlyList<DeviceOption> DeviceOptions { get; private set; }

        public DeviceOption? SelectedDevice { get; private set; }

        public string? BoundDeviceId { get; private set; }

        public IReadOnlyList<EqOption> AllEqOptions { get; private set; }

        public IReadOnlyList<EqOption> EqMenuOptions { get; private set; }

        public EqOption? SelectedEq { get; private set; }

        public MediaBrush AccentBrush { get; }

        public Visibility DeviceVisibility => BindingChannel is null ? Visibility.Collapsed : Visibility.Visible;

        public Visibility MasterVisibility => BindingChannel is null ? Visibility.Visible : Visibility.Collapsed;

        public Visibility EqVisibility => BindingChannel is null ? Visibility.Collapsed : Visibility.Visible;

        public bool HasEqOptions => AllEqOptions.Count > 0;

        public string EqSearchText
        {
            get => _eqSearchText;
            set
            {
                var normalized = value ?? string.Empty;
                if (_eqSearchText == normalized)
                {
                    return;
                }

                _eqSearchText = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EqMenuOptions));
            }
        }

        public int Percent
        {
            get => _percent;
            set
            {
                if (_percent == value)
                {
                    return;
                }

                _percent = Math.Clamp(value, 0, 100);
                if (_percent > 0)
                {
                    _lastNonZeroPercent = _percent;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(PercentLabel));
            }
        }

        public int LastNonZeroPercent => _lastNonZeroPercent;

        public int SnapshotPercent { get; }

        public bool Muted
        {
            get => _muted;
            set
            {
                if (_muted == value)
                {
                    return;
                }

                _muted = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MuteToolTip));
            }
        }

        public string PercentLabel => $"{Percent}%";

        public string MuteToolTip => Muted ? "取消静音" : "静音";

        public void RememberNonZero(int percent)
        {
            if (percent > 0)
            {
                _lastNonZeroPercent = Math.Clamp(percent, 1, 100);
            }
        }

        public void ApplyExternalState(int volumePercent, bool muted, bool updateDisplayedVolume)
        {
            RememberNonZero(volumePercent);
            Muted = muted || volumePercent == 0;
            if (updateDisplayedVolume)
            {
                Percent = Muted ? 0 : volumePercent;
            }
        }

        public void UpdateDeviceOptions(IReadOnlyList<DeviceOption> deviceOptions, string? boundDeviceId)
        {
            var optionsChanged = DeviceOptions.Count != deviceOptions.Count ||
                                 !DeviceOptions.SequenceEqual(deviceOptions);
            var bindingChanged = BoundDeviceId != boundDeviceId;
            if (!optionsChanged && !bindingChanged)
            {
                return;
            }

            DeviceOptions = deviceOptions;
            BoundDeviceId = boundDeviceId;
            SelectedDevice = deviceOptions.FirstOrDefault(device => device.Id == boundDeviceId);
            OnPropertyChanged(nameof(DeviceOptions));
            OnPropertyChanged(nameof(SelectedDevice));
        }

        public void UpdateEqOptions(IReadOnlyList<EqOption> eqOptions, string? selectedEqId)
        {
            var optionsChanged = AllEqOptions.Count != eqOptions.Count || !AllEqOptions.SequenceEqual(eqOptions);
            var selectedChanged = SelectedEq?.Id != selectedEqId;
            if (!optionsChanged && !selectedChanged)
            {
                return;
            }

            AllEqOptions = eqOptions;
            SelectedEq = eqOptions.FirstOrDefault(option => option.Id == selectedEqId);
            EqMenuOptions = BuildEqMenuOptions();
            OnPropertyChanged(nameof(AllEqOptions));
            OnPropertyChanged(nameof(EqMenuOptions));
            OnPropertyChanged(nameof(SelectedEq));
            OnPropertyChanged(nameof(HasEqOptions));
        }

        public bool UpdateSelectedEq(EqOption? selectedEq, bool preserveLoadedOptions)
        {
            if (SelectedEq?.Id == selectedEq?.Id && SelectedEq?.Name == selectedEq?.Name)
            {
                return false;
            }

            var selectedWasMissing = preserveLoadedOptions && selectedEq is not null &&
                                     !AllEqOptions.Any(option => option.Id == selectedEq.Id);
            AllEqOptions = preserveLoadedOptions
                ? selectedWasMissing
                    ? AllEqOptions.Concat([selectedEq!]).ToArray()
                    : AllEqOptions
                : selectedEq is null ? Array.Empty<EqOption>() : [selectedEq];
            SelectedEq = selectedEq;
            EqMenuOptions = BuildEqMenuOptions();
            OnPropertyChanged(nameof(AllEqOptions));
            OnPropertyChanged(nameof(EqMenuOptions));
            OnPropertyChanged(nameof(SelectedEq));
            OnPropertyChanged(nameof(HasEqOptions));
            return selectedWasMissing;
        }

        public void SetEqLoading(bool isLoading)
        {
            if (_isEqLoading == isLoading)
            {
                return;
            }

            _isEqLoading = isLoading;
            EqMenuOptions = BuildEqMenuOptions();
            OnPropertyChanged(nameof(EqMenuOptions));
        }

        private IReadOnlyList<EqOption> BuildEqMenuOptions()
        {
            if (_isEqLoading)
            {
                return SelectedEq is null ? [EqOption.Loading] : [SelectedEq, EqOption.Loading];
            }

            if (string.IsNullOrWhiteSpace(_eqSearchText))
            {
                // Keep the active preset visible even when it is not a favorite: a ComboBox
                // cannot reliably retain a SelectedItem that is absent from its ItemsSource.
                var defaultOptions = AllEqOptions.Where(option => option.IsFavorite).ToList();
                if (SelectedEq is { IsFavorite: false } selected &&
                    !defaultOptions.Any(option => option.Id == selected.Id))
                {
                    defaultOptions.Insert(0, selected);
                }

                return AppendOtherEqOptions(defaultOptions, GetMatchingNonFavoriteEqOptions());
            }

            var favorites = AllEqOptions
                .Where(option => option.IsFavorite && option.Name.Contains(_eqSearchText, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return AppendOtherEqOptions(favorites, GetMatchingNonFavoriteEqOptions());
        }

        private IReadOnlyList<EqOption> GetMatchingNonFavoriteEqOptions() => AllEqOptions
            .Where(option => !option.IsFavorite &&
                             (string.IsNullOrWhiteSpace(_eqSearchText) ||
                              option.Name.Contains(_eqSearchText, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        private static IReadOnlyList<EqOption> AppendOtherEqOptions(
            IReadOnlyList<EqOption> favorites,
            IEnumerable<EqOption> otherOptions)
        {
            var others = otherOptions.ToArray();
            if (favorites.Count == 0)
            {
                return others;
            }

            if (others.Length == 0)
            {
                return favorites;
            }

            return favorites.Concat([EqOption.Separator]).Concat(others).ToArray();
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed record TrackDragSession(Slider Slider, Track Track, Action Complete);

    private abstract class StreamerVolumeRow : INotifyPropertyChanged
    {
        private int _percent;
        private int _lastNonZeroPercent = 50;
        private bool _muted;
        private IReadOnlyList<EqOption> _eqOptions = [];
        private EqOption? _selectedEq;

        protected StreamerVolumeRow(string displayName, MediaBrush accentBrush)
        {
            DisplayName = displayName;
            AccentBrush = accentBrush;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string DisplayName { get; }

        public MediaBrush AccentBrush { get; }

        public string ActiveKey { get; protected set; } = string.Empty;

        public virtual string? EqChannel => null;

        public Visibility EqVisibility => EqChannel is null ? Visibility.Collapsed : Visibility.Visible;

        public IReadOnlyList<EqOption> EqOptions
        {
            get => _eqOptions;
            private set
            {
                _eqOptions = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EqOptions)));
            }
        }

        public EqOption? SelectedEq
        {
            get => _selectedEq;
            set
            {
                if (Equals(_selectedEq, value))
                {
                    return;
                }

                _selectedEq = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedEq)));
            }
        }

        public void SetEqOptions(IReadOnlyList<EqOption> options)
        {
            var current = SelectedEq;
            EqOptions = options;
            if (current is not null)
            {
                SelectedEq = options.FirstOrDefault(option =>
                    string.Equals(option.Id, current.Id, StringComparison.Ordinal)) ?? current;
            }
        }

        public void SetSelectedEq(EqOption? option) => SelectedEq = option;

        public int Percent
        {
            get => _percent;
            set
            {
                var clamped = Math.Clamp(value, 0, 100);
                if (_percent == clamped)
                {
                    return;
                }

                _percent = clamped;
                if (_percent > 0)
                {
                    _lastNonZeroPercent = _percent;
                }

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Percent)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PercentLabel)));
            }
        }

        public int LastNonZeroPercent => _lastNonZeroPercent;

        public bool Muted
        {
            get => _muted;
            set
            {
                if (_muted == value)
                {
                    return;
                }

                _muted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Muted)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MuteToolTip)));
            }
        }

        public string PercentLabel => $"{Percent}%";

        public string MuteToolTip => Muted ? "取消静音" : "静音";

        public virtual void ApplyConfirmed(VolumeState state) => ApplyState(state, updateDisplayedVolume: true);

        protected void ApplyState(VolumeState state, bool updateDisplayedVolume)
        {
            var percent = (int)Math.Round(Math.Clamp(state.Volume, 0f, 1f) * 100);
            if (percent > 0)
            {
                _lastNonZeroPercent = percent;
            }

            Muted = state.Muted || percent == 0;
            if (updateDisplayedVolume)
            {
                Percent = Muted ? 0 : percent;
            }
        }
    }

    private sealed class StreamerChannelRow : StreamerVolumeRow
    {
        private readonly Dictionary<string, VolumeState> _mixes = new(StringComparer.Ordinal);

        public StreamerChannelRow(string channel, string displayName, MediaBrush accentBrush)
            : base(displayName, accentBrush)
        {
            Channel = channel;
        }

        public string Channel { get; }

        public override string? EqChannel =>
            string.Equals(Channel, "master", StringComparison.Ordinal) ? null : Channel;

        public string Mix { get; private set; } = StreamingMix;

        public void PreviewMixVolume(string mix, int percent)
        {
            var muted = _mixes.TryGetValue(mix, out var state) && state.Muted;
            _mixes[mix] = new VolumeState(percent / 100f, muted);
            Percent = percent;
        }

        public void UpdateMix(string mix, VolumeState state) => _mixes[mix] = state;

        public void ShowMix(string mix, bool updateDisplayedVolume)
        {
            Mix = mix;
            ActiveKey = $"stream|{mix}|{Channel}";
            if (_mixes.TryGetValue(mix, out var state))
            {
                ApplyState(state, updateDisplayedVolume);
            }
        }

        public override void ApplyConfirmed(VolumeState state)
        {
            _mixes[Mix] = state;
            base.ApplyConfirmed(state);
        }
    }

    private sealed class StreamSourceChip(string mix, string role, string label, bool isEnabled)
        : INotifyPropertyChanged
    {
        private bool _isEnabled = isEnabled;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Mix { get; } = mix;

        public string Role { get; } = role;

        public string Label { get; } = label;

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value)
                {
                    return;
                }

                _isEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            }
        }
    }

    private sealed record DeviceOption(string Name, string Id);

    private sealed record EqOption(string Name, string Id, bool IsFavorite, bool IsSeparator = false, bool IsLoading = false)
    {
        public static EqOption Separator { get; } = new(string.Empty, string.Empty, false, true);

        public static EqOption Loading { get; } = new("正在加载 EQ…", string.Empty, false, false, true);
    }

    private sealed record RoutedApplication(
        string DisplayName,
        int ProcessId,
        int SessionCount,
        string SourceRole)
    {
        public string SessionCountLabel => SessionCount > 1 ? $"×{SessionCount}" : string.Empty;
    }

    private sealed class RoutingLane : INotifyPropertyChanged
    {
        private bool _isDropTarget;
        private string _dropHint = string.Empty;

        public RoutingLane(
            string name,
            string role,
            string deviceId,
            IReadOnlyList<RoutedApplication> applications,
            MediaBrush accentBrush)
        {
            Name = name;
            Role = role;
            DeviceId = deviceId;
            Applications = applications;
            AccentBrush = accentBrush;
            var tinted = ((SolidColorBrush)accentBrush).Color;
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(96, tinted.R, tinted.G, tinted.B));
        }

        public string Name { get; }

        public string Role { get; }

        public string DeviceId { get; }

        public IReadOnlyList<RoutedApplication> Applications { get; }

        public MediaBrush AccentBrush { get; }

        public MediaBrush BorderBrush { get; }

        public string AppCountLabel => $"{Applications.Count} 个";

        public Visibility EmptyVisibility =>
            Applications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public string DropHint => _dropHint;

        public Visibility DropHintVisibility => _isDropTarget ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void ShowDropHint()
        {
            _dropHint = $"释放以移动到 {Name}";
            _isDropTarget = true;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DropHint)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DropHintVisibility)));
        }

        public void HideDropHint()
        {
            if (!_isDropTarget)
            {
                return;
            }

            _isDropTarget = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DropHintVisibility)));
        }
    }
}
