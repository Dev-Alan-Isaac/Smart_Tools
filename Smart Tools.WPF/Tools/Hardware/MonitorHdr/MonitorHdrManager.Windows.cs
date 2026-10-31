using Microsoft.Win32;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The Windows-only half of the HDR &amp; Color Depth Checker. Two sources are combined per screen, matched by the GDI name (\\.\DISPLAY1):
    /// the display-config API (HDR / advanced colour, colour format, SDR white level, the monitor's name and EDID) and DXGI
    /// (<c>IDXGIOutput6.GetDesc1</c>: bits per colour, colour space, primaries and luminance). DXGI is called through its vtable so no package is needed.
    /// </summary>
    public static partial class MonitorHdrManager
    {
        // ---- display config ----------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DcHeader { public int Type; public uint Size; public Luid Adapter; public uint Id; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DcPath
        {
            public Luid SourceAdapter; public uint SourceId; public uint SourceModeIndex; public uint SourceStatus;
            public Luid TargetAdapter; public uint TargetId; public uint TargetModeIndex; public uint OutputTechnology; public uint Rotation; public uint Scaling;
            public uint RefreshNumerator, RefreshDenominator; public uint ScanLineOrdering; public int TargetAvailable; public uint TargetStatus;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential, Size = 64)]
        private struct DcMode { public uint InfoType; public uint Id; public Luid Adapter; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DcSourceName
        {
            public DcHeader Header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DcTargetName
        {
            public DcHeader Header;
            public uint Flags; public uint OutputTechnology; public ushort EdidManufacturer; public ushort EdidProduct; public uint ConnectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DcColorInfo { public DcHeader Header; public uint Value; public int Encoding; public uint Bits; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DcColorInfo2 { public DcHeader Header; public uint Value; public int Encoding; public uint Bits; public int ActiveMode; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DcSdrWhite { public DcHeader Header; public uint Level; }

        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] DcPath[] pathArray, ref uint modes, [Out] DcMode[] modeArray, IntPtr topology);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetInfo(ref DcSourceName info);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetInfo(ref DcTargetName info);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetInfo(ref DcColorInfo info);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetInfo(ref DcColorInfo2 info);
        [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] private static extern int GetInfo(ref DcSdrWhite info);

        private const uint QdcOnlyActivePaths = 2;

        private static DcHeader Header(int type, int size, Luid adapter, uint id) => new() { Type = type, Size = (uint)size, Adapter = adapter, Id = id };

        private sealed record ConfigReading(string FriendlyName, byte[]? Edid, bool Supported, bool Enabled, bool WideColor, int Bits, string Format, double SdrWhiteNits);

        private static Dictionary<string, ConfigReading> ReadDisplayConfig()
        {
            var result = new Dictionary<string, ConfigReading>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0) return result;
                var paths = new DcPath[pathCount];
                var modes = new DcMode[modeCount];
                if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return result;

                for (int i = 0; i < pathCount; i++)
                {
                    var path = paths[i];
                    var source = new DcSourceName { Header = Header(1, Marshal.SizeOf<DcSourceName>(), path.SourceAdapter, path.SourceId) };
                    if (GetInfo(ref source) != 0 || string.IsNullOrEmpty(source.GdiName)) continue;

                    var target = new DcTargetName { Header = Header(2, Marshal.SizeOf<DcTargetName>(), path.TargetAdapter, path.TargetId) };
                    string friendly = string.Empty;
                    byte[]? edid = null;
                    if (GetInfo(ref target) == 0)
                    {
                        friendly = target.FriendlyName ?? string.Empty;
                        edid = ReadEdid(target.DevicePath);
                    }

                    bool supported = false, enabled = false, wide = false;
                    int bits = 0;
                    string format = string.Empty;

                    var color2 = new DcColorInfo2 { Header = Header(14, Marshal.SizeOf<DcColorInfo2>(), path.TargetAdapter, path.TargetId) };
                    if (GetInfo(ref color2) == 0)
                    {
                        // Windows 11 24H2 and later: HDR and wide colour are reported on their own.
                        supported = (color2.Value & 0x10) != 0;
                        enabled = supported && color2.ActiveMode == 2;
                        wide = (color2.Value & 0x80) != 0 || color2.ActiveMode >= 1;
                        bits = (int)color2.Bits;
                        format = EncodingName(color2.Encoding);
                    }
                    else
                    {
                        var color = new DcColorInfo { Header = Header(9, Marshal.SizeOf<DcColorInfo>(), path.TargetAdapter, path.TargetId) };
                        if (GetInfo(ref color) == 0)
                        {
                            supported = (color.Value & 1) != 0;
                            enabled = (color.Value & 2) != 0 && (color.Value & 4) == 0;   // bit 2 = wide colour enforced, not HDR
                            wide = (color.Value & 6) != 0;
                            bits = (int)color.Bits;
                            format = EncodingName(color.Encoding);
                        }
                    }

                    double sdrWhite = 0;
                    var white = new DcSdrWhite { Header = Header(11, Marshal.SizeOf<DcSdrWhite>(), path.TargetAdapter, path.TargetId) };
                    if (GetInfo(ref white) == 0 && white.Level > 0) sdrWhite = white.Level / 1000.0 * 80.0;

                    result[source.GdiName] = new ConfigReading(friendly, edid, supported, enabled, wide, bits, format, sdrWhite);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
            {
                Log.Info($"DisplayConfig: {ex.Message}");
            }
            return result;
        }

        private static string EncodingName(int encoding) => encoding switch
        {
            0 => "RGB",
            1 => "YCbCr 4:4:4",
            2 => "YCbCr 4:2:2",
            3 => "YCbCr 4:2:0",
            4 => "Intensity",
            _ => string.Empty,
        };

        /// <summary>The EDID Windows keeps for a monitor, found from its device path (\\?\DISPLAY#GSM5B09#5&amp;...#{guid}).</summary>
        private static byte[]? ReadEdid(string? devicePath)
        {
            if (string.IsNullOrEmpty(devicePath)) return null;
            try
            {
                string path = devicePath.TrimStart('\\', '?');
                int guid = path.LastIndexOf("#{", StringComparison.Ordinal);
                if (guid > 0) path = path[..guid];
                path = path.Replace('#', '\\');
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{path}\Device Parameters");
                return key?.GetValue("EDID") as byte[];
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
            {
                Log.Info($"EDID: {ex.Message}");
                return null;
            }
        }

        // ---- DXGI -------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OutputDesc1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
            public uint BitsPerColor;
            public int ColorSpace;
            public float RedX, RedY, GreenX, GreenY, BlueX, BlueY, WhiteX, WhiteY;
            public float MinLuminance, MaxLuminance, MaxFullFrameLuminance;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr obj);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumFn(IntPtr self, uint index, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDesc1Fn(IntPtr self, out OutputDesc1 desc);

        [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);

        private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IidOutput6 = new("068346e8-aaec-4b84-add7-137f513f77a1");

        // vtable slots: IUnknown 0-2, IDXGIObject 3-6, then each interface adds its own methods.
        private const int SlotFactoryEnumAdapters1 = 12;
        private const int SlotAdapterEnumOutputs = 7;
        private const int SlotOutput6GetDesc1 = 27;

        private static T Slot<T>(IntPtr com, int index) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), index * IntPtr.Size));

        private static void Release(IntPtr com)
        {
            if (com != IntPtr.Zero) Slot<ReleaseFn>(com, 2)(com);
        }

        private static Dictionary<string, OutputDesc1> ReadDxgi()
        {
            var result = new Dictionary<string, OutputDesc1>(StringComparer.OrdinalIgnoreCase);
            IntPtr factory = IntPtr.Zero;
            try
            {
                var iid = IidFactory1;
                if (CreateDXGIFactory1(ref iid, out factory) != 0 || factory == IntPtr.Zero) return result;

                var enumAdapters = Slot<EnumFn>(factory, SlotFactoryEnumAdapters1);
                for (uint a = 0; enumAdapters(factory, a, out IntPtr adapter) == 0; a++)
                {
                    try
                    {
                        var enumOutputs = Slot<EnumFn>(adapter, SlotAdapterEnumOutputs);
                        for (uint o = 0; enumOutputs(adapter, o, out IntPtr output) == 0; o++)
                        {
                            IntPtr output6 = IntPtr.Zero;
                            try
                            {
                                var iid6 = IidOutput6;
                                if (Slot<QueryInterfaceFn>(output, 0)(output, ref iid6, out output6) != 0) continue;   // older Windows 10: no Output6
                                if (Slot<GetDesc1Fn>(output6, SlotOutput6GetDesc1)(output6, out var desc) == 0 && !string.IsNullOrEmpty(desc.DeviceName))
                                    result.TryAdd(desc.DeviceName, desc);
                            }
                            finally
                            {
                                Release(output6);
                                Release(output);
                            }
                        }
                    }
                    finally
                    {
                        Release(adapter);
                    }
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or MarshalDirectiveException or AccessViolationException)
            {
                Log.Info($"DXGI: {ex.Message}");
            }
            finally
            {
                Release(factory);
            }
            return result;
        }

        // ---- the result -------------------------------------------------------------

        /// <summary>Every attached screen with its colour readings; anything Windows doesn't report is left at zero / empty.</summary>
        public static List<MonitorHdrInfo> ReadScreens()
        {
            var config = ReadDisplayConfig();
            var dxgi = ReadDxgi();
            var list = new List<MonitorHdrInfo>();

            foreach (var s in MonitorTestManager.ListScreens())
            {
                config.TryGetValue(s.DeviceName, out var c);
                bool haveDxgi = dxgi.TryGetValue(s.DeviceName, out var d);

                // DXGI's colour space says what the desktop is using right now: 12 = HDR10 (PQ, Rec. 2020), 10 = scRGB.
                bool hdrSpace = haveDxgi && d.ColorSpace is 12 or 10;
                bool enabled = (c?.Enabled ?? false) || hdrSpace;
                bool supported = (c?.Supported ?? false) || enabled;

                int currentBits = haveDxgi && d.BitsPerColor > 0 ? (int)d.BitsPerColor : c?.Bits ?? 0;
                var primaries = haveDxgi
                    ? new MonitorHdrPrimaries(new(d.RedX, d.RedY), new(d.GreenX, d.GreenY), new(d.BlueX, d.BlueY), new(d.WhiteX, d.WhiteY))
                    : new MonitorHdrPrimaries(default, default, default, default);

                list.Add(new MonitorHdrInfo(
                    s.DeviceName, s.Name, c?.FriendlyName ?? string.Empty, s.X, s.Y, s.Width, s.Height, s.IsPrimary, s.RefreshHz,
                    currentBits, ParseEdidBits(c?.Edid), c?.Format ?? string.Empty,
                    supported, enabled, c?.WideColor ?? false,
                    haveDxgi ? d.MinLuminance : 0, haveDxgi ? d.MaxLuminance : 0, haveDxgi ? d.MaxFullFrameLuminance : 0,
                    c?.SdrWhiteNits ?? 0, primaries));
            }
            return list;
        }
    }
}
