using Smart_Tools.WPF.Models;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using IDataObject = System.Windows.IDataObject;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The Windows half: a hidden message window that Windows tells when the clipboard changes, and reading / writing the clipboard.</summary>
    public sealed partial class ClipboardHistoryManager
    {
        private const int WmClipboardUpdate = 0x031D;
        private static readonly IntPtr HwndMessage = new(-3);   // a window that only receives messages

        private const string FormatExclude = "ExcludeClipboardContentFromMonitorProcessing";
        private const string FormatCanInclude = "CanIncludeInClipboardHistory";

        private HwndSource? _source;
        private bool _disposed;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        /// <summary>Starts listening. Call on the UI thread.</summary>
        public void Start()
        {
            if (_source != null || _disposed) return;
            var parameters = new HwndSourceParameters("SmartToolsClipboardHistory") { ParentWindow = HwndMessage, Width = 0, Height = 0, WindowStyle = 0 };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            AddClipboardFormatListener(_source.Handle);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmClipboardUpdate && IsRecording)
            {
                handled = true;
                _ = CaptureAsync();
            }
            return IntPtr.Zero;
        }

        /// <summary>Reads the clipboard; another program may still hold it for a moment, so a few quick retries.</summary>
        private async Task CaptureAsync()
        {
            for (int attempt = 0; attempt < 6 && !_disposed; attempt++)
            {
                try
                {
                    Capture();
                    return;
                }
                catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
                {
                    await Task.Delay(40);
                }
            }
        }

        private void Capture()
        {
            var data = Clipboard.GetDataObject();
            if (data == null || IsMarkedPrivate(data)) return;

            if (Clipboard.ContainsText())
            {
                AddText(Clipboard.GetText(), DateTime.Now);
            }
            else if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bitmap)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                AddImage(stream.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight, DateTime.Now);
            }
        }

        /// <summary>Password managers (and our own Password Vault) mark what they copy so clipboard tools skip it.</summary>
        private static bool IsMarkedPrivate(IDataObject data)
        {
            if (data.GetDataPresent(FormatExclude)) return true;
            if (!data.GetDataPresent(FormatCanInclude)) return false;
            return data.GetData(FormatCanInclude) switch
            {
                MemoryStream stream => stream.Length > 0 && stream.ToArray()[0] == 0,
                byte[] bytes => bytes.Length > 0 && bytes[0] == 0,
                int value => value == 0,
                _ => false,
            };
        }

        /// <summary>Puts an item back on the clipboard. False if another program is using it.</summary>
        public bool CopyBack(ClipboardItem item)
        {
            try
            {
                if (!item.IsImage)
                {
                    Clipboard.SetDataObject(new DataObject(System.Windows.DataFormats.UnicodeText, item.Text), true);
                    return true;
                }
                if (item.ImagePng == null) return false;
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(item.ImagePng);
                image.EndInit();
                image.Freeze();
                Clipboard.SetImage(image);
                return true;
            }
            catch (ExternalException)
            {
                return false;
            }
        }

        /// <summary>Stops listening; the list stays. <see cref="Start"/> can be called again.</summary>
        public void Stop()
        {
            if (_source != null)
            {
                RemoveClipboardFormatListener(_source.Handle);
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _items.Clear();
        }
    }
}
