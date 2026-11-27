using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaRecorder.Core.Devices;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace MediaRecorder.App.Services.Recording;

/// <summary>
/// Listet Video- und Audioeingänge auf und meldet An- und Abstecken (DeviceWatcher),
/// damit ein abgezogener USB-Grabber sofort erkannt wird.
/// </summary>
public sealed class CaptureDeviceService : IDisposable
{
    private DeviceWatcher? _videoWatcher;
    private DeviceWatcher? _audioWatcher;

    /// <summary>Geräteliste hat sich geändert (Thread beliebig).</summary>
    public event EventHandler? DevicesChanged;

    /// <summary>Ein Gerät wurde entfernt; Argument = Geräte-Id.</summary>
    public event EventHandler<string>? DeviceRemoved;

    public async Task<IReadOnlyList<CaptureDeviceInfo>> GetVideoDevicesAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        return devices
            .Where(d => d.IsEnabled)
            .Select(d => new CaptureDeviceInfo(d.Id, d.Name, CaptureDeviceKind.Video, d.IsDefault))
            .OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name)
            .ToList();
    }

    public async Task<IReadOnlyList<CaptureDeviceInfo>> GetAudioDevicesAsync()
    {
        // MediaCapture erwartet die Audio-Geräte-Id aus diesem Selektor.
        var selector = MediaDevice.GetAudioCaptureSelector();
        var defaultId = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
        var devices = await DeviceInformation.FindAllAsync(selector);
        return devices
            .Where(d => d.IsEnabled)
            .Select(d => new CaptureDeviceInfo(d.Id, d.Name, CaptureDeviceKind.Audio, d.Id == defaultId))
            .OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name)
            .ToList();
    }

    public void StartWatching()
    {
        if (_videoWatcher is not null) return;
        _videoWatcher = CreateWatcher(DeviceInformation.CreateWatcher(DeviceClass.VideoCapture));
        _audioWatcher = CreateWatcher(DeviceInformation.CreateWatcher(MediaDevice.GetAudioCaptureSelector()));
    }

    private DeviceWatcher CreateWatcher(DeviceWatcher w)
    {
        bool enumerated = false;
        w.EnumerationCompleted += (_, _) => enumerated = true;
        w.Added += (_, _) => { if (enumerated) DevicesChanged?.Invoke(this, EventArgs.Empty); };
        w.Updated += (_, _) => { if (enumerated) DevicesChanged?.Invoke(this, EventArgs.Empty); };
        w.Removed += (_, u) =>
        {
            DeviceRemoved?.Invoke(this, u.Id);
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        };
        w.Start();
        return w;
    }

    public void Dispose()
    {
        foreach (var w in new[] { _videoWatcher, _audioWatcher })
        {
            if (w is null) continue;
            try
            {
                if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                    w.Stop();
            }
            catch { /* Watcher beim Beenden ignorieren */ }
        }
        _videoWatcher = _audioWatcher = null;
    }
}
