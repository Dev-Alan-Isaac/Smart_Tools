using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Makes a window see-through for the Glass theme: the desktop behind it is blurred and tinted by Windows ("acrylic"),
    /// and the app's translucent surfaces (Themes/Glass.xaml) sit on top of that.
    ///
    /// - Windows 11 (build 22621+): DWM system backdrop (DWMWA_SYSTEMBACKDROP_TYPE = transient window / acrylic).
    /// - Windows 10 1803+ and older Windows 11: the window accent policy (acrylic blur behind).
    /// - Anything else, or a failed call: the Glass theme still works, over a plain dark window.
    ///
    /// <see cref="Apply"/> is safe to call at any time, with either value; turning glass off puts the opaque window back.
    /// Copy of CoinTrace's GlassWindow (same behaviour), plus handling for the app's other windows.
    /// </summary>
    public static class GlassWindow
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMSBT_NONE = 1;
        private const int DWMSBT_TRANSIENTWINDOW = 3;   // acrylic

        private static readonly Color FallbackColor = Color.FromRgb(0x11, 0x15, 0x1F);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins { public int Left, Right, Top, Bottom; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        private const int WCA_ACCENT_POLICY = 19;
        private const int ACCENT_DISABLED = 0;
        private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

        /// <summary>Which backdrop the last <see cref="Apply"/> got: "dwm", "accent" or "none" (for logs and tests).</summary>
        public static string LastMode { get; private set; } = "none";

        /// <summary>Turns the see-through backdrop on or off for <paramref name="window"/>. Returns true when Windows accepted it.</summary>
        public static bool Apply(Window window, bool glass)
        {
            LastMode = "none";
            try
            {
                var helper = new WindowInteropHelper(window);
                IntPtr hwnd = helper.EnsureHandle();   // creates the handle if the window is not shown yet (nothing appears)
                var source = HwndSource.FromHwnd(hwnd);
                if (source?.CompositionTarget == null) return false;

                bool ok = glass ? Enable(window, hwnd, source) : Disable(window, hwnd, source);
                return ok;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or COMException)
            {
                System.Diagnostics.Debug.WriteLine($"[GlassWindow] {ex.Message}");
                if (glass) UseFallback(window);
                return false;
            }
        }

        private static bool Enable(Window window, IntPtr hwnd, HwndSource source)
        {
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            bool accepted = false;
            if (Environment.OSVersion.Version.Build >= 22621)
            {
                int type = DWMSBT_TRANSIENTWINDOW;
                accepted = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) == 0;
                if (accepted) LastMode = "dwm";
            }

            if (!accepted && Environment.OSVersion.Version.Build >= 17134)
            {
                accepted = SetAccent(hwnd, ACCENT_ENABLE_ACRYLICBLURBEHIND, 0x40101826u) == 0;
                if (accepted) LastMode = "accent";
            }

            if (!accepted)
            {
                UseFallback(window);
                return false;
            }

            // The blur only shows where the window itself paints nothing: transparent composition + frame extended over the client area.
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
            if (WindowChrome.GetWindowChrome(window) is { } chrome) chrome.GlassFrameThickness = new Thickness(-1);
            window.Background = Brushes.Transparent;
            return true;
        }

        private static bool Disable(Window window, IntPtr hwnd, HwndSource source)
        {
            int none = DWMSBT_NONE;
            if (Environment.OSVersion.Version.Build >= 22621) DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
            SetAccent(hwnd, ACCENT_DISABLED, 0);
            var margins = new Margins();
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
            if (WindowChrome.GetWindowChrome(window) is { } chrome) chrome.GlassFrameThickness = new Thickness(0);
            source.CompositionTarget.BackgroundColor = Colors.White;
            window.SetResourceReference(Window.BackgroundProperty, "ContentBg");
            return true;
        }

        private static int SetAccent(IntPtr hwnd, int state, uint color)
        {
            var policy = new AccentPolicy { AccentState = state, AccentFlags = 2, GradientColor = color };
            int size = Marshal.SizeOf(policy);
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, pointer, false);
                var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = pointer, SizeOfData = size };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0 ? 0 : 1;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }

        // ------------------------------------------------------------------ the app's other windows

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> Patched = new();
        private static readonly SolidColorBrush SecondaryBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x1A, 0x28)));

        private static SolidColorBrush Freeze(SolidColorBrush brush) { brush.Freeze(); return brush; }

        /// <summary>
        /// Dialog and tool windows have no blurred backdrop, so under the translucent Glass surfaces they would show Windows' default
        /// (white) window colour. While the Glass theme is on, a window whose background is the theme's ContentBg gets a plain
        /// dark one instead; turning Glass off puts ContentBg back. Windows with their own background (mini players) are left alone.
        /// </summary>
        public static void FixSecondaryWindows(bool glass)
        {
            var app = System.Windows.Application.Current;
            if (app == null) return;
            foreach (Window window in app.Windows)
            {
                if (ReferenceEquals(window, app.MainWindow)) continue;
                if (glass)
                {
                    if (!Patched.TryGetValue(window, out _) && ReferenceEquals(window.Background, window.TryFindResource("ContentBg")))
                    {
                        Patched.Add(window, true);
                        window.Background = SecondaryBrush;
                    }
                }
                else if (Patched.TryGetValue(window, out _))
                {
                    Patched.Remove(window);
                    window.SetResourceReference(Window.BackgroundProperty, "ContentBg");
                }
            }
        }

        /// <summary>Call once at start-up: windows opened later while Glass is on get the same fix when they load.</summary>
        public static void WatchNewWindows(Func<bool> isGlass)
        {
            System.Windows.EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
            {
                if (isGlass() && s is Window window && !ReferenceEquals(window, System.Windows.Application.Current?.MainWindow)) FixSecondaryWindows(true);
            }));
        }

        /// <summary>No blur available: keep the translucent surfaces but over a plain dark window instead of an unpainted one.</summary>
        private static void UseFallback(Window window) => window.Background = new SolidColorBrush(FallbackColor);
    }
}
