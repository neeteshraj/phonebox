#if WINDOWS
#pragma warning disable CS0618 // WasapiOut: its replacement (WasapiPlayer) is new in NAudio 3; the plain shared-mode player is all this needs
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace PhoneBox.Agent;

/// <summary>
/// The phone mic's playback: WASAPI shared mode on the first active render endpoint whose name contains the configured
/// text ("CABLE Input"), whatever the default device is. Windows converts 48 kHz stereo s16 to the device's mix format.
/// </summary>
internal sealed class WasapiPcmOutput : IPcmOutput, IWaveProvider
{
    private readonly WasapiOut _out;
    private readonly MMDevice _device;
    private readonly PcmJitterBuffer _buffer;
    private volatile bool _faulted;

    public WaveFormat WaveFormat { get; } = new(ScrcpyAudio.SampleRate, 16, ScrcpyAudio.Channels);
    public string DeviceName { get; }
    public bool Faulted => _faulted;

    public WasapiPcmOutput(string name, int latencyMs, PcmJitterBuffer buffer)
    {
        _buffer = buffer;
        using var devices = new MMDeviceEnumerator();
        var all = devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var match = all.FirstOrDefault(d => d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase));
        // every handle but the one kept is released, also when none matched (this is retried every few seconds)
        foreach (var d in all.Where(d => d != match)) d.Dispose();
        _device = match ?? throw new IOException($"no active playback device named like \"{name}\"");
        try
        {
            DeviceName = _device.FriendlyName;
            _out = new WasapiOut(_device, AudioClientShareMode.Shared, true, latencyMs);
        }
        catch { _device.Dispose(); throw; }
        _out.PlaybackStopped += (_, _) => _faulted = true;
        try { _out.Init(this); _out.Play(); }
        catch { _out.Dispose(); _device.Dispose(); throw; }
    }

    public int Read(Span<byte> buffer)
    {
        _buffer.Read(buffer);
        return buffer.Length;
    }

    /// <summary>The names of the active playback devices (for the phone mic's setup check).</summary>
    public static IReadOnlyList<string> PlaybackDeviceNames()
    {
        using var devices = new MMDeviceEnumerator();
        var names = new List<string>();
        foreach (var d in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) using (d) names.Add(d.FriendlyName);
        return names;
    }

    public void Dispose()
    {
        _faulted = true;
        // a device that was just removed can throw from any of these; each one is still tried
        try { _out.Stop(); } catch { }
        try { _out.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
    }
}
#endif
