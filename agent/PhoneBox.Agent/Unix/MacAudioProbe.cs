using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>Reads the currently selected macOS output. DDC is only used when that output is a matching display.</summary>
internal static class MacAudioProbe
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Global = 0x676c6f62; // 'glob'
    private const uint Output = 0x6f757470; // 'outp'
    private const uint DefaultOutput = 0x644f7574; // 'dOut'
    private const uint Name = 0x6c6e616d; // 'lnam'
    private const uint Volume = 0x766f6c6d; // 'volm'
    private const uint Mute = 0x6d757465; // 'mute'

    [StructLayout(LayoutKind.Sequential)]
    private struct Address(uint selector, uint scope, uint element)
    {
        public uint Selector = selector;
        public uint Scope = scope;
        public uint Element = element;
    }

    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int GetUInt(uint id, ref Address address, uint qualifierSize, nint qualifier, ref uint size, out uint value);
    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int GetFloat(uint id, ref Address address, uint qualifierSize, nint qualifier, ref uint size, out float value);
    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int GetPointer(uint id, ref Address address, uint qualifierSize, nint qualifier, ref uint size, out nint value);
    [DllImport(CoreAudio, EntryPoint = "AudioObjectIsPropertySettable")]
    private static extern int IsSettable(uint id, ref Address address, out byte settable);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(nint value, byte[] buffer, nint bufferSize, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);

    private static uint? UInt(uint id, uint selector, uint scope, uint element)
    {
        var address = new Address(selector, scope, element);
        uint size = sizeof(uint);
        return GetUInt(id, ref address, 0, 0, ref size, out var value) == 0 ? value : null;
    }

    private static float? Float(uint id, uint selector, uint scope, uint element)
    {
        var address = new Address(selector, scope, element);
        uint size = sizeof(float);
        return GetFloat(id, ref address, 0, 0, ref size, out var value) == 0 && float.IsFinite(value) ? value : null;
    }

    public static bool CanControl(uint device)
    {
        bool Writable(uint selector, uint element)
        {
            var address = new Address(selector, Output, element);
            return IsSettable(device, ref address, out var writable) == 0 && writable != 0;
        }
        return (Writable(Volume, 0) || Writable(Volume, 1) || Writable(Volume, 2)) && Writable(Mute, 0);
    }

    private static string? DeviceName(uint device) => CfString(device, Name);

    /// <summary>A CFString property of an audio object (name 'lnam', UID 'uid ').</summary>
    private static string? CfString(uint device, uint selector)
    {
        var address = new Address(selector, Global, 0);
        uint size = (uint)IntPtr.Size;
        if (GetPointer(device, ref address, 0, 0, ref size, out var cfString) != 0 || cfString == 0) return null;
        try
        {
            var buffer = new byte[1024];
            if (!CFStringGetCString(cfString, buffer, buffer.Length, 0x08000100)) return null;
            var length = Array.IndexOf(buffer, (byte)0);
            return length >= 0 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
        }
        finally { CFRelease(cfString); }
    }

    public static (uint Id, string Name, int? Volume, bool? Muted)? SelectedOutput()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        try
        {
            var device = UInt(1, DefaultOutput, Global, 0);
            if (device is null or 0) return null;
            var scalar = Float(device.Value, Volume, Output, 0);
            if (scalar is null)
            {
                var channels = new[] { Float(device.Value, Volume, Output, 1), Float(device.Value, Volume, Output, 2) }
                    .Where(value => value is not null).Select(value => value!.Value).ToArray();
                if (channels.Length > 0) scalar = channels.Average();
            }
            var muted = UInt(device.Value, Mute, Output, 0);
            return (device.Value, DeviceName(device.Value) ?? "", scalar is { } s ? (int)Math.Round(Math.Clamp(s, 0, 1) * 100) : null,
                muted is { } m ? m != 0 : null);
        }
        catch { return null; }
    }

    // ---------- output devices ----------

    private const uint Devices = 0x64657623; // 'dev#'
    private const uint Streams = 0x73746d23; // 'stm#'
    private const uint Uid = 0x75696420; // 'uid '
    private const uint Transport = 0x7472616e; // 'tran'
    private const uint DefaultSystemOutput = 0x734f7574; // 'sOut'

    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyDataSize")]
    private static extern int GetSize(uint id, ref Address address, uint qualifierSize, nint qualifier, out uint size);
    [DllImport(CoreAudio, EntryPoint = "AudioObjectGetPropertyData")]
    private static extern int GetArray(uint id, ref Address address, uint qualifierSize, nint qualifier, ref uint size, [Out] uint[] values);
    [DllImport(CoreAudio, EntryPoint = "AudioObjectSetPropertyData")]
    private static extern int SetUInt(uint id, ref Address address, uint qualifierSize, nint qualifier, uint size, ref uint value);

    public sealed record OutputDevice(string Id, string Name, string Transport, bool Current);

    /// <summary>The CoreAudio transport type as the phone's names it.</summary>
    internal static string TransportName(uint fourcc) => fourcc switch
    {
        0x626c746e => "builtin",                  // 'bltn'
        0x75736220 => "usb",                      // 'usb '
        0x626c7565 or 0x626c6561 => "bluetooth",  // 'blue', 'blea' (LE)
        0x68646d69 => "hdmi",                     // 'hdmi'
        0x64707274 => "displayport",              // 'dprt'
        0x61697270 => "airplay",                  // 'airp'
        0x76697274 or 0x67727570 => "virtual",    // 'virt', 'grup' (aggregate)
        _ => "other",
    };

    /// <summary>Every device with output streams, with its CoreAudio id; the default output marked current.</summary>
    private static List<(uint Device, OutputDevice Output)> ScanOutputs()
    {
        var list = new List<(uint, OutputDevice)>();
        var address = new Address(Devices, Global, 0);
        if (GetSize(1, ref address, 0, 0, out var size) != 0 || size == 0) return list;
        var ids = new uint[size / sizeof(uint)];
        if (GetArray(1, ref address, 0, 0, ref size, ids) != 0) return list;
        var current = UInt(1, DefaultOutput, Global, 0);
        foreach (var id in ids.Take((int)(size / sizeof(uint))))
        {
            var streams = new Address(Streams, Output, 0);
            if (GetSize(id, ref streams, 0, 0, out var bytes) != 0 || bytes == 0) continue;
            if (CfString(id, Uid) is not { Length: > 0 } uid) continue;
            list.Add((id, new(uid, DeviceName(id) ?? uid, TransportName(UInt(id, Transport, Global, 0) ?? 0), id == current)));
        }
        return list;
    }

    /// <summary>The output devices; [] off macOS or on failure.</summary>
    public static IReadOnlyList<OutputDevice> Outputs()
    {
        if (!OperatingSystem.IsMacOS()) return [];
        try { return ScanOutputs().Select(o => o.Output).ToList(); } catch { return []; }
    }

    /// <summary>Makes the output with this UID the default (and system / alert) output; false when there is none.</summary>
    public static bool SetDefaultOutput(string uid)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            if (ScanOutputs().FirstOrDefault(o => o.Output.Id == uid).Device is not (> 0 and var device)) return false;
            var output = new Address(DefaultOutput, Global, 0);
            if (SetUInt(1, ref output, 0, 0, sizeof(uint), ref device) != 0) return false;
            var system = new Address(DefaultSystemOutput, Global, 0);
            SetUInt(1, ref system, 0, 0, sizeof(uint), ref device); // best effort: some devices cannot play alerts
            return true;
        }
        catch { return false; }
    }

    private static uint _displayDevice;
    private static string _displayName = "";
    private static int? _displayIndex;
    private static long _displayAt;

    public static int? DisplayVolume(uint device, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Sh.Which("m1ddc") is null) return null;
        if (_displayDevice != device || _displayName != name || Environment.TickCount64 - _displayAt > 30_000)
        {
            _displayDevice = device;
            _displayName = name;
            _displayAt = Environment.TickCount64;
            _displayIndex = null;
            var list = Sh.RunWithTimeout(5000, "m1ddc", "display", "list");
            if (list is not null)
            {
                foreach (var line in list.Split('\n'))
                {
                    var match = Regex.Match(line, @"^\[(\d+)\] (.+?) \(");
                    if (match.Success && string.Equals(match.Groups[2].Value.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(match.Groups[1].Value, out var index)) { _displayIndex = index; break; }
                }
            }
        }
        if (_displayIndex is not { } display) return null;
        var current = Sh.RunWithTimeout(3000, "m1ddc", "display", display.ToString(), "get", "volume")?.Trim();
        var maximum = Sh.RunWithTimeout(3000, "m1ddc", "display", display.ToString(), "max", "volume")?.Trim();
        return int.TryParse(current, out var value) && int.TryParse(maximum, out var max) && max > 0
            ? (int)Math.Round(Math.Clamp(value / (double)max, 0, 1) * 100) : null;
    }
}
