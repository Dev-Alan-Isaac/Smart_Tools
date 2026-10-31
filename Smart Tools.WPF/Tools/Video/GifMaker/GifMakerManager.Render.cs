using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>GIF Maker - the WPF half: turning frame grabs into images and playing a finished GIF.</summary>
    public static partial class GifMakerManager
    {
        /// <summary>PNG bytes (a frame grab) → a frozen image for the trim preview. Null when the bytes aren't a picture.</summary>
        public static BitmapSource? ToBitmap(byte[]? png)
        {
            if (png == null || png.Length == 0) return null;
            try
            {
                var image = new BitmapImage();
                using var stream = new MemoryStream(png);
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Plays a GIF in an Image: WPF only shows a GIF's first frame. Frames
    /// are drawn one at a time onto a canvas (each frame is often just the
    /// rectangle that changed, at an offset, with see-through pixels), the
    /// GIF's disposal rule is applied between frames, and the result is
    /// copied into one <see cref="WriteableBitmap"/>. Memory stays small: the
    /// decoder keeps the frames in their 8-bit form.
    /// </summary>
    public sealed class GifAnimator
    {
        private sealed record Frame(BitmapSource Source, int Left, int Top, int Delay, int Disposal);

        private readonly List<Frame> _frames;
        private readonly byte[] _canvas;
        private byte[]? _saved;
        private int _index = -1;

        public int Width { get; }
        public int Height { get; }
        public int FrameCount => _frames.Count;

        /// <summary>The picture to show (updated in place by <see cref="Advance"/>).</summary>
        public WriteableBitmap Bitmap { get; }

        private GifAnimator(List<Frame> frames, int width, int height)
        {
            _frames = frames;
            Width = width;
            Height = height;
            _canvas = new byte[width * height * 4];
            Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        }

        /// <summary>Reads the whole file into memory first, so the GIF isn't locked while it's shown. Null when it can't be decoded.</summary>
        public static GifAnimator? Load(string path)
        {
            try
            {
                return FromBytes(File.ReadAllBytes(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>A GIF already in memory (the loop preview). Null when it can't be decoded.</summary>
        public static GifAnimator? FromBytes(byte[] bytes)
        {
            try
            {
                var decoder = new GifBitmapDecoder(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0) return null;

                var frames = new List<Frame>(decoder.Frames.Count);
                int width = 0, height = 0;
                foreach (var f in decoder.Frames)
                {
                    var meta = f.Metadata as BitmapMetadata;
                    int left = Query(meta, "/imgdesc/Left");
                    int top = Query(meta, "/imgdesc/Top");
                    int delay = Query(meta, "/grctlext/Delay") * 10;
                    int disposal = Query(meta, "/grctlext/Disposal");
                    if (delay < 20) delay = 100;   // what browsers do with 0 and 1 cs
                    frames.Add(new Frame(f, left, top, delay, disposal));
                    width = Math.Max(width, left + f.PixelWidth);
                    height = Math.Max(height, top + f.PixelHeight);
                }

                // The logical screen can be bigger than every frame.
                try
                {
                    if (decoder.Metadata is BitmapMetadata m)
                    {
                        width = Math.Max(width, Query(m, "/logscrdesc/Width"));
                        height = Math.Max(height, Query(m, "/logscrdesc/Height"));
                    }
                }
                catch (NotSupportedException)
                {
                    // no container metadata: the frames' extent is used
                }

                if (width <= 0 || height <= 0) return null;
                var animator = new GifAnimator(frames, width, height);
                animator.Advance();
                return animator;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
            {
                return null;
            }
        }

        private static int Query(BitmapMetadata? meta, string query)
        {
            try
            {
                return meta?.GetQuery(query) switch
                {
                    ushort u => u,
                    byte b => b,
                    int i => i,
                    _ => 0,
                };
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException or InvalidOperationException)
            {
                return 0;
            }
        }

        /// <summary>Back to the first frame.</summary>
        public void Reset()
        {
            _index = -1;
            _saved = null;
            Array.Clear(_canvas);
            Advance();
        }

        /// <summary>Draws the next frame (wrapping round at the end) and returns how long to show it, in ms.</summary>
        public int Advance()
        {
            // Dispose of the frame on screen as it asked, before drawing the next one.
            if (_index >= 0)
            {
                var previous = _frames[_index];
                if (previous.Disposal == 2) ClearRect(previous);
                else if (previous.Disposal == 3 && _saved != null) Buffer.BlockCopy(_saved, 0, _canvas, 0, _canvas.Length);
            }

            _index++;
            if (_index >= _frames.Count)
            {
                _index = 0;
                Array.Clear(_canvas);
            }

            var frame = _frames[_index];
            if (frame.Disposal == 3) _saved = (byte[])_canvas.Clone();
            DrawFrame(frame);
            Bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), _canvas, Width * 4, 0);
            return frame.Delay;
        }

        public int CurrentDelay => _index >= 0 ? _frames[_index].Delay : 100;

        private void DrawFrame(Frame frame)
        {
            var source = frame.Source.Format == PixelFormats.Bgra32
                ? frame.Source
                : new FormatConvertedBitmap(frame.Source, PixelFormats.Bgra32, null, 0);
            int fw = source.PixelWidth, fh = source.PixelHeight;
            var pixels = new byte[fw * fh * 4];
            source.CopyPixels(pixels, fw * 4, 0);

            for (int y = 0; y < fh; y++)
            {
                int cy = frame.Top + y;
                if (cy < 0 || cy >= Height) continue;
                for (int x = 0; x < fw; x++)
                {
                    int cx = frame.Left + x;
                    if (cx < 0 || cx >= Width) continue;
                    int s = (y * fw + x) * 4;
                    if (pixels[s + 3] < 128) continue;   // the GIF's see-through colour: keep what's below
                    int d = (cy * Width + cx) * 4;
                    _canvas[d] = pixels[s];
                    _canvas[d + 1] = pixels[s + 1];
                    _canvas[d + 2] = pixels[s + 2];
                    _canvas[d + 3] = 255;
                }
            }
        }

        private void ClearRect(Frame frame)
        {
            int fw = frame.Source.PixelWidth, fh = frame.Source.PixelHeight;
            for (int y = Math.Max(0, frame.Top); y < Math.Min(Height, frame.Top + fh); y++)
            {
                int x0 = Math.Max(0, frame.Left), x1 = Math.Min(Width, frame.Left + fw);
                if (x1 > x0) Array.Clear(_canvas, (y * Width + x0) * 4, (x1 - x0) * 4);
            }
        }
    }
}
