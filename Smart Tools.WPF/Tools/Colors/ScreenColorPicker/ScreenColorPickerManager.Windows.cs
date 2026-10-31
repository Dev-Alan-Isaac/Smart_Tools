using Smart_Tools.WPF.Models;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The Windows-only half: a GDI picture of every monitor.</summary>
    public static partial class ScreenColorPickerManager
    {
        /// <summary>
        /// A picture of the whole virtual screen (all monitors) in real pixels. The eyedropper
        /// works on this frozen picture, so what is picked is exactly what was on screen -
        /// even a menu or tooltip that would close the moment the app took focus.
        /// </summary>
        public static ScreenColorImage CaptureVirtualScreen()
        {
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new byte[bitmap.Width * 4 * bitmap.Height];
                for (int y = 0; y < bitmap.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width * 4, bitmap.Width * 4);

                // GDI leaves alpha at 0 on some drivers; the colours are what matter here.
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                return new ScreenColorImage(pixels, bitmap.Width, bitmap.Height);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
    }
}
