using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Models
{
    /// <summary>The characters used, from empty to full. Bad Apple looks best with Smooth or Sharp.</summary>
    public enum AsciiRamp { Smooth, Detailed, Blocks, Sharp }

    public sealed class AsciiVideoSettings
    {
        public int Columns { get; set; } = 120;
        public int Fps { get; set; } = 30;
        public AsciiRamp Ramp { get; set; } = AsciiRamp.Smooth;
        /// <summary>White characters on black (true) or black on white.</summary>
        public bool WhiteOnBlack { get; set; } = true;
        public bool MakeMp4 { get; set; } = true;
        public bool MakeGif { get; set; }
        public bool MakeText { get; set; } = true;
        public bool KeepSound { get; set; } = true;
    }

    /// <summary>What FFprobe says about the video.</summary>
    public sealed record AsciiVideoProbe(int Width, int Height, double DurationSeconds, bool HasAudio);

    /// <summary>The character grid and the size of one character cell in the finished video.</summary>
    public sealed record AsciiLayout(int Columns, int Rows, int CellWidth, int CellHeight)
    {
        public int Width => Columns * CellWidth;

        public int Height => Rows * CellHeight;

        /// <summary>Bytes of one decoded frame: one grey pixel per character.</summary>
        public int CellBytes => Columns * Rows;
    }
}

namespace Smart_Tools.WPF.Controllers
{
    using Smart_Tools.WPF.Models;

    /// <summary>
    /// ASCII video as maths and text: the character ramps, the grid size, turning one small grey frame into characters, stamping the
    /// characters into a picture, the text file format, the console player, and the FFmpeg / yt-dlp command lines. No process, no drawing
    /// API, so every part is tested on its own.
    /// </summary>
    public static class AsciiVideoMath
    {
        public const int MinColumns = 40;
        public const int MaxColumns = 240;
        public const double MaxSeconds = 10 * 60;   // Bad Apple (3:39) fits with room to spare

        public static readonly int[] FpsChoices = { 10, 15, 24, 30 };

        /// <summary>Sparse to dense. The space must come first: a dark pixel on a dark background is "nothing".</summary>
        public static string RampText(AsciiRamp ramp) => ramp switch
        {
            AsciiRamp.Detailed => " .'`^\",:;Il!i><~+_-?][}{1)(|\\/tfjrxnuvczXYUJCLQ0OZmwqpdbkhao*#MW&8%B@$",
            AsciiRamp.Blocks => " ░▒▓█",
            AsciiRamp.Sharp => " #",
            _ => " .:-=+*#%@",
        };

        // =================================================================
        //  The grid
        // =================================================================

        /// <summary>
        /// How many rows of characters keep the picture's shape. A character cell is taller than wide (<paramref name="cellAspect"/> =
        /// width / height, about 0.5), so there are fewer rows than the picture's proportions alone would give.
        /// </summary>
        public static int RowsFor(int columns, int sourceWidth, int sourceHeight, double cellAspect)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0 || cellAspect <= 0) return Math.Max(1, columns / 2);
            double rows = columns * ((double)sourceHeight / sourceWidth) * cellAspect;
            return Math.Clamp((int)Math.Round(rows), 1, 400);
        }

        /// <summary>
        /// The layout of the finished video: about <paramref name="targetWidth"/> pixels wide, each cell a whole number of pixels, and an
        /// even width and height (H.264 needs that). <paramref name="cellAspect"/> is the font's cell width / height.
        /// </summary>
        public static AsciiLayout Layout(int columns, int sourceWidth, int sourceHeight, double cellAspect, int targetWidth = 1440)
        {
            columns = Math.Clamp(columns, MinColumns, MaxColumns);
            int rows = RowsFor(columns, sourceWidth, sourceHeight, cellAspect);
            int cellW = Math.Max(4, targetWidth / columns);
            int cellH = Math.Max(6, (int)Math.Round(cellW / Math.Max(0.2, cellAspect)));
            if (columns * cellW % 2 == 1) cellW++;           // odd columns x odd cell: make the cell even
            if (rows * cellH % 2 == 1) cellH++;
            return new AsciiLayout(columns, rows, cellW, cellH);
        }

        // =================================================================
        //  One frame
        // =================================================================

        /// <summary>
        /// One grey frame (one byte per character cell, 0 = black) as characters. With white on black a bright pixel gets a dense
        /// character; with black on white a dark pixel does.
        /// </summary>
        public static char[] FrameToChars(ReadOnlySpan<byte> grey, string ramp, bool whiteOnBlack)
        {
            var chars = new char[grey.Length];
            int last = ramp.Length - 1;
            for (int i = 0; i < grey.Length; i++)
            {
                int level = whiteOnBlack ? grey[i] : 255 - grey[i];
                chars[i] = ramp[(level * last + 127) / 255];
            }
            return chars;
        }

        /// <summary>The characters as lines of text (no trailing spaces are trimmed: every line is the same width).</summary>
        public static string ToLines(char[] chars, int columns)
        {
            var sb = new StringBuilder(chars.Length + chars.Length / Math.Max(1, columns) * 2);
            for (int i = 0; i < chars.Length; i += columns)
            {
                sb.Append(chars, i, Math.Min(columns, chars.Length - i));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Stamps the characters into a grey picture: each character's mask (cellW x cellH, 0..255 coverage) is copied into its cell.
        /// White on black copies the mask; black on white copies its inverse. A character with no mask (the space) leaves the background.
        /// </summary>
        public static void Compose(char[] chars, AsciiLayout layout, IReadOnlyDictionary<char, byte[]> masks, bool whiteOnBlack, byte[] picture)
        {
            int w = layout.Width;
            byte background = whiteOnBlack ? (byte)0 : (byte)255;
            Array.Fill(picture, background);
            for (int r = 0; r < layout.Rows; r++)
            {
                for (int c = 0; c < layout.Columns; c++)
                {
                    char ch = chars[r * layout.Columns + c];
                    if (ch == ' ' || !masks.TryGetValue(ch, out var mask)) continue;
                    int top = r * layout.CellHeight * w + c * layout.CellWidth;
                    for (int y = 0; y < layout.CellHeight; y++)
                    {
                        int dst = top + y * w, src = y * layout.CellWidth;
                        if (whiteOnBlack) Buffer.BlockCopy(mask, src, picture, dst, layout.CellWidth);
                        else
                            for (int x = 0; x < layout.CellWidth; x++) picture[dst + x] = (byte)(255 - mask[src + x]);
                    }
                }
            }
        }

        // =================================================================
        //  The text file and its console player
        // =================================================================

        public const string FileMagic = "ASCIIVIDEO 1";

        /// <summary>The first line of frames.txt: "ASCIIVIDEO 1 columns rows fps frames". Frames follow, each exactly rows lines.</summary>
        public static string Header(int columns, int rows, int fps, int frames) =>
            string.Create(CultureInfo.InvariantCulture, $"{FileMagic} {columns} {rows} {fps} {frames}");

        public static bool TryReadHeader(string line, out int columns, out int rows, out int fps, out int frames)
        {
            columns = rows = fps = frames = 0;
            var parts = (line ?? "").Trim().Split(' ');
            return parts.Length == 6 && parts[0] == "ASCIIVIDEO" && parts[1] == "1"
                && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out columns) && columns > 0
                && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out rows) && rows > 0
                && int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out fps) && fps > 0
                && int.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out frames) && frames >= 0;
        }

        /// <summary>
        /// A PowerShell player for frames.txt: sizes the console, plays sound.wav if it is there, and shows each frame at its time (it
        /// skips frames when the console is slow, so picture and sound stay together). Esc stops it.
        /// </summary>
        public static string PlayerScript() => @"# ASCII video player made by Smart Tools. Plays frames.txt (and sound.wav when present) in this console window. Esc stops.
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$lines = [System.IO.File]::ReadAllLines((Join-Path $here 'frames.txt'), [System.Text.Encoding]::UTF8)
$head = $lines[0].Split(' ')
if ($head.Length -ne 6 -or $head[0] -ne 'ASCIIVIDEO') { Write-Host 'frames.txt is not an ASCII video.'; exit 1 }
$cols = [int]$head[2]; $rows = [int]$head[3]; $fps = [int]$head[4]; $count = [int]$head[5]
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
try { [Console]::BufferWidth = [Math]::Max([Console]::BufferWidth, $cols + 1); [Console]::WindowWidth = [Math]::Min($cols + 1, [Console]::LargestWindowWidth); [Console]::WindowHeight = [Math]::Min($rows + 1, [Console]::LargestWindowHeight) } catch { }
[Console]::CursorVisible = $false
Clear-Host
$player = $null
$sound = Join-Path $here 'sound.wav'
if (Test-Path $sound) { $player = New-Object System.Media.SoundPlayer $sound; $player.Load(); $player.Play() }
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$shown = -1
try {
  while ($true) {
    if ([Console]::KeyAvailable -and [Console]::ReadKey($true).Key -eq 'Escape') { break }
    $frame = [int][Math]::Floor($clock.Elapsed.TotalSeconds * $fps)
    if ($frame -ge $count) { break }
    if ($frame -ne $shown) {
      $start = 1 + $frame * $rows
      [Console]::SetCursorPosition(0, 0)
      [Console]::Write([string]::Join(""`n"", $lines, $start, $rows))
      $shown = $frame
    } else { Start-Sleep -Milliseconds 2 }
  }
} finally {
  if ($player) { $player.Stop() }
  [Console]::CursorVisible = $true
}
";

        /// <summary>Double-click to play: runs the player script with PowerShell for this one run, whatever the PC's script setting.</summary>
        public static string PlayerCmd() => "@echo off\r\nrem ASCII video player made by Smart Tools. Double-click to play; Esc stops.\r\npowershell -NoProfile -ExecutionPolicy Bypass -File \"%~dp0play.ps1\"\r\n";

        // =================================================================
        //  Command lines
        // =================================================================

        private static string Seconds(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);

        public static IReadOnlyList<string> ProbeArgs(string input) => new[]
        {
            "-v", "error", "-show_entries", "stream=codec_type,width,height:format=duration", "-of", "json", input,
        };

        /// <summary>Reads FFprobe's JSON: the first video stream's size, the length, and whether there is sound. Null when there is no video.</summary>
        public static AsciiVideoProbe? ParseProbe(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                int w = 0, h = 0;
                bool audio = false;
                if (doc.RootElement.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                    foreach (var st in streams.EnumerateArray())
                    {
                        string type = st.TryGetProperty("codec_type", out var t) ? t.GetString() ?? "" : "";
                        if (type == "audio") audio = true;
                        if (type == "video" && w == 0 && st.TryGetProperty("width", out var sw) && sw.TryGetInt32(out int ww)
                            && st.TryGetProperty("height", out var sh) && sh.TryGetInt32(out int hh))
                        {
                            w = ww;
                            h = hh;
                        }
                    }
                if (w <= 0 || h <= 0) return null;
                double duration = 0;
                if (doc.RootElement.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var d)
                    && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dd)) duration = dd;
                return new AsciiVideoProbe(w, h, duration, audio);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>Range options for an input: start and length in seconds (none when null).</summary>
        private static IEnumerable<string> Range(double? start, double? length)
        {
            if (start is > 0) { yield return "-ss"; yield return Seconds(start.Value); }
            if (length is > 0) { yield return "-t"; yield return Seconds(length.Value); }
        }

        /// <summary>Decode to one grey byte per character cell, at the chosen frame rate, on stdout.</summary>
        public static IReadOnlyList<string> DecodeArgs(string input, double? start, double? length, int fps, AsciiLayout layout)
        {
            var args = new List<string> { "-v", "error", "-nostdin" };
            args.AddRange(Range(start, length));
            args.AddRange(new[]
            {
                "-i", input, "-an",
                "-vf", string.Create(CultureInfo.InvariantCulture, $"fps={fps},scale={layout.Columns}:{layout.Rows}:flags=area,format=gray"),
                "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1",
            });
            return args;
        }

        /// <summary>Encode the drawn frames from stdin as an MP4, with the original sound of the same stretch when asked.</summary>
        public static IReadOnlyList<string> EncodeArgs(string input, double? start, double? length, int fps, AsciiLayout layout, bool sound, string output)
        {
            var args = new List<string>
            {
                "-v", "error", "-y",
                "-f", "rawvideo", "-pix_fmt", "gray", "-s", $"{layout.Width}x{layout.Height}", "-r", fps.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0",
            };
            if (sound)
            {
                args.AddRange(Range(start, length));
                args.AddRange(new[] { "-i", input, "-map", "0:v:0", "-map", "1:a:0?", "-c:a", "aac", "-b:a", "160k", "-shortest" });
            }
            args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart", output });
            return args;
        }

        /// <summary>A GIF from the finished MP4 (no sound in a GIF), smaller and with its own palette.</summary>
        public static IReadOnlyList<string> GifArgs(string mp4, int fps, string output) => new[]
        {
            "-v", "error", "-y", "-i", mp4,
            "-vf", string.Create(CultureInfo.InvariantCulture, $"fps={Math.Min(fps, 15)},scale=640:-2:flags=area,split[a][b];[a]palettegen=max_colors=16[p];[b][p]paletteuse=dither=none"),
            output,
        };

        /// <summary>The sound of the same stretch as a WAV, for the console player.</summary>
        public static IReadOnlyList<string> SoundArgs(string input, double? start, double? length, string output)
        {
            var args = new List<string> { "-v", "error", "-y" };
            args.AddRange(Range(start, length));
            args.AddRange(new[] { "-i", input, "-vn", "-ac", "2", "-ar", "44100", "-c:a", "pcm_s16le", output });
            return args;
        }

        /// <summary>yt-dlp: one video, at most 720p (more is wasted on characters), as an MP4 file in <paramref name="folder"/>.</summary>
        public static IReadOnlyList<string> DownloadArgs(string url, string folder, string ffmpegFolder) => new[]
        {
            "-f", "bv*[height<=720]+ba/b[height<=720]/bv*+ba/b", "--merge-output-format", "mp4",
            "--no-playlist", "--newline", "--no-warnings", "--windows-filenames", "--no-mtime",
            "--progress-template", "download:PROGRESS|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
            "--ffmpeg-location", ffmpegFolder,
            "-o", System.IO.Path.Combine(folder, "%(title).100B.%(ext)s"),
            "--", url,
        };
    }
}
