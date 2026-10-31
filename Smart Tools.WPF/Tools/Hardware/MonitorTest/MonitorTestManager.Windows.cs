using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The Windows-only half of the Monitor Test: the screens Windows knows about, and a probe that measures how often the display refreshes.</summary>
    public static partial class MonitorTestManager
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public ushort SpecVersion, DriverVersion, Size, DriverExtra;
            public uint Fields;
            public int PositionX, PositionY;
            public uint DisplayOrientation, DisplayFixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public ushort LogPixels;
            public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency, ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode mode);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();

        private const int EnumCurrentSettings = -1;

        /// <summary>Every attached screen, with the refresh rate and colour depth Windows is using on it right now.</summary>
        public static List<MonitorScreenInfo> ListScreens()
        {
            var list = new List<MonitorScreenInfo>();
            int number = 1;
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                int hz = 0, bpp = 0;
                try
                {
                    var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
                    if (EnumDisplaySettings(screen.DeviceName, EnumCurrentSettings, ref mode))
                    {
                        hz = (int)mode.DisplayFrequency;
                        bpp = (int)mode.BitsPerPel;
                    }
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    Log.Info($"EnumDisplaySettings: {ex.Message}");
                }

                var b = screen.Bounds;
                string name = $"{number}: {b.Width} × {b.Height}";
                list.Add(new MonitorScreenInfo(screen.DeviceName, name, b.X, b.Y, b.Width, b.Height, screen.Primary, hz, bpp));
                number++;
            }
            return list;
        }

        /// <summary>
        /// Waits for the display to refresh, over and over, and notes the time of each. Windows' compositor refreshes with the
        /// monitor, so the spacing is the real refresh interval, whatever the program draws. Runs for about <paramref name="seconds"/> seconds.
        /// </summary>
        public static List<double> MeasureRefreshTimes(double seconds, CancellationToken cancel)
        {
            var times = new List<double>(512);
            long start = Stopwatch.GetTimestamp();
            double freq = Stopwatch.Frequency;
            var previous = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    if (DwmFlush() != 0) break;   // composition is off or unavailable
                    double t = (Stopwatch.GetTimestamp() - start) / freq;
                    times.Add(t);
                    if (t >= seconds) break;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Log.Info($"DwmFlush: {ex.Message}");
            }
            finally
            {
                Thread.CurrentThread.Priority = previous;
            }
            return times;
        }
    }
}
