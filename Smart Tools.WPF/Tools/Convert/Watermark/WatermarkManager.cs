using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Batch Watermarker logic that needs no WPF, so it compiles in a plain net8.0 project: which engine takes a file,
    /// where every copy of the mark goes (3 x 3 grid, or tiled), output names, the built-in and saved presets, the app-owned
    /// copies of logos, and the settings. Drawing the mark and the three engines (pictures, PDFs, videos) are in the other partial files.
    /// </summary>
    public static partial class WatermarkManager
    {
        // =================================================================
        //  Which engine
        // =================================================================

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp", ".gif" };

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".wmv", ".webm", ".flv", ".ts", ".mts", ".m2ts" };

        /// <summary>Null when the file is none of the three kinds.</summary>
        public static WatermarkFileKind? KindOf(string path)
        {
            string ext = Path.GetExtension(path);
            if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase)) return WatermarkFileKind.Pdf;
            if (ImageExtensions.Contains(ext)) return WatermarkFileKind.Image;
            if (VideoExtensions.Contains(ext)) return WatermarkFileKind.Video;
            return null;
        }

        public static bool IsSupported(string path) => KindOf(path) != null;

        // =================================================================
        //  Names
        // =================================================================

        /// <summary>Videos are written as MP4 unless they are MP4 / MOV / M4V / MKV already (the codecs of the rest may not fit another container).</summary>
        public static string OutputExtension(string sourcePath, WatermarkFileKind kind)
        {
            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (kind != WatermarkFileKind.Video) return ext;
            return ext is ".mp4" or ".mov" or ".m4v" or ".mkv" ? ext : ".mp4";
        }

        /// <summary>
        /// Where results go when the user hasn't picked a folder, by kind of file:
        /// Pictures\Smart Tools\Watermark, Videos\Smart Tools\Watermark, Documents\Smart Tools\Watermark (PDFs).
        /// </summary>
        public static string DefaultFolderFor(WatermarkFileKind kind)
        {
            var library = kind switch
            {
                WatermarkFileKind.Image => Environment.SpecialFolder.MyPictures,
                WatermarkFileKind.Video => Environment.SpecialFolder.MyVideos,
                _ => Environment.SpecialFolder.MyDocuments,
            };
            string root = Environment.GetFolderPath(library);
            if (string.IsNullOrEmpty(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(root, "Smart Tools", "Watermark");
        }

        /// <summary>"photo.jpg" becomes "photo-watermarked.jpg" (the folder decides where, and a taken name gets " (2)").</summary>
        public static string OutputFileName(string sourcePath, WatermarkFileKind kind) =>
            Path.GetFileNameWithoutExtension(sourcePath) + "-watermarked" + OutputExtension(sourcePath, kind);

        // =================================================================
        //  Layout
        // =================================================================

        /// <summary>The centre of one copy of the mark, in file pixels.</summary>
        public readonly record struct MarkCopy(double CenterX, double CenterY);

        /// <summary>The size of the box around a w x h rectangle turned by the angle.</summary>
        public static (double Width, double Height) RotatedSize(double width, double height, double degrees)
        {
            double r = degrees * Math.PI / 180, c = Math.Abs(Math.Cos(r)), s = Math.Abs(Math.Sin(r));
            return (width * c + height * s, width * s + height * c);
        }

        /// <summary>Most copies a tiled layout makes (a tiny mark on a huge file would otherwise make millions).</summary>
        public const int MaxCopies = 3000;

        /// <summary>
        /// Where every copy of the mark goes on a file of the given size. A single mark sits on the chosen spot of the
        /// 3 x 3 grid, inside the margin (the centre ignores the margin; a mark too big for the margin is centred on that axis).
        /// Tiled repeats the mark in brick rows, with the whole pattern turned by the mark's own angle around the file's centre,
        /// keeping only the copies that touch the file.
        /// </summary>
        public static List<MarkCopy> Layout(int targetWidth, int targetHeight, double markWidth, double markHeight, WatermarkSettings s)
        {
            double w = targetWidth, h = targetHeight;
            var result = new List<MarkCopy>();
            if (w <= 0 || h <= 0 || markWidth <= 0 || markHeight <= 0) return result;
            var (bw, bh) = RotatedSize(markWidth, markHeight, s.Rotation);

            if (s.Placement != WatermarkPlacement.Tiled)
            {
                double margin = Math.Clamp(s.MarginPercent, 0, 50) / 100 * Math.Min(w, h);
                int p = (int)s.Placement;
                int column = p % 3, row = p / 3;
                double cx = column == 1 ? w / 2 : column == 0 ? margin + bw / 2 : w - margin - bw / 2;
                double cy = row == 1 ? h / 2 : row == 0 ? margin + bh / 2 : h - margin - bh / 2;
                if (bw + 2 * margin > w) cx = w / 2;
                if (bh + 2 * margin > h) cy = h / 2;
                result.Add(new MarkCopy(cx, cy));
                return result;
            }

            double gap = Math.Max(4, Math.Clamp(s.TileGapPercent, 0, 1000) / 100 * markHeight);
            double stepX = markWidth + gap, stepY = markHeight + gap;
            double radius = Math.Sqrt(w * w + h * h) / 2 + Math.Sqrt(markWidth * markWidth + markHeight * markHeight);
            int rows = (int)Math.Ceiling(radius / stepY) + 1, columns = (int)Math.Ceiling(radius / stepX) + 1;
            double angle = s.Rotation * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);

            for (int j = -rows; j <= rows; j++)
            {
                double offset = (j & 1) != 0 ? stepX / 2 : 0;
                for (int i = -columns; i <= columns; i++)
                {
                    double px = i * stepX + offset, py = j * stepY;
                    double cx = w / 2 + px * cos - py * sin, cy = h / 2 + px * sin + py * cos;
                    if (cx + bw / 2 <= 0 || cx - bw / 2 >= w || cy + bh / 2 <= 0 || cy - bh / 2 >= h) continue;   // misses the file
                    result.Add(new MarkCopy(cx, cy));
                    if (result.Count >= MaxCopies) return result;
                }
            }
            return result;
        }

        /// <summary>The part of the file the copies cover, so only that part has to be drawn and laid over (whole pixels, inside the file).</summary>
        public static (int X, int Y, int Width, int Height) CoveredArea(int targetWidth, int targetHeight, double markWidth, double markHeight,
            IReadOnlyList<MarkCopy> copies, double rotation)
        {
            if (copies.Count == 0) return (0, 0, 0, 0);
            var (bw, bh) = RotatedSize(markWidth, markHeight, rotation);
            double left = copies.Min(c => c.CenterX - bw / 2), right = copies.Max(c => c.CenterX + bw / 2);
            double top = copies.Min(c => c.CenterY - bh / 2), bottom = copies.Max(c => c.CenterY + bh / 2);
            int x0 = Math.Clamp((int)Math.Floor(left) - 1, 0, targetWidth), y0 = Math.Clamp((int)Math.Floor(top) - 1, 0, targetHeight);
            int x1 = Math.Clamp((int)Math.Ceiling(right) + 1, 0, targetWidth), y1 = Math.Clamp((int)Math.Ceiling(bottom) + 1, 0, targetHeight);
            return (x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        }

        /// <summary>The text height in pixels for a file of this size (never under 6 px, so it can always be read).</summary>
        public static double TextPixelSize(int targetWidth, int targetHeight, WatermarkSettings s) =>
            Math.Max(6, Math.Clamp(s.SizePercent, 0.5, 100) / 100 * Math.Min(targetWidth, targetHeight));

        /// <summary>The picture mark's width in pixels, never wider than the file.</summary>
        public static double ImagePixelWidth(int targetWidth, WatermarkSettings s) =>
            Math.Clamp(Math.Clamp(s.SizePercent, 0.5, 100) / 100 * targetWidth, 4, targetWidth);

        // =================================================================
        //  Colour
        // =================================================================

        /// <summary>Reads #RRGGBB (the # is optional) into red, green, blue. False when it is not a colour.</summary>
        public static bool TryParseColor(string? text, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            string t = (text ?? "").Trim().TrimStart('#');
            if (t.Length == 3) t = string.Concat(t.Select(c => new string(c, 2)));
            if (t.Length != 6 || !t.All(Uri.IsHexDigit)) return false;
            uint v = uint.Parse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            (r, g, b) = ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
            return true;
        }

        /// <summary>White text gets a dark edge and dark text a light one: whichever contrasts.</summary>
        public static bool IsLightColor(string? hex) =>
            TryParseColor(hex, out byte r, out byte g, out byte b) && (0.2126 * r + 0.7152 * g + 0.0722 * b) > 140;

        /// <summary>Puts every value in range and falls back to safe ones, so a damaged settings file or preset can't break drawing.</summary>
        public static WatermarkSettings Normalize(WatermarkSettings s)
        {
            var n = s.Clone();
            if (!Enum.IsDefined(n.Kind)) n.Kind = WatermarkKind.Text;
            if (!Enum.IsDefined(n.Placement)) n.Placement = WatermarkPlacement.BottomRight;
            n.Text = (n.Text ?? "").Length > 200 ? n.Text![..200] : n.Text ?? "";
            if (string.IsNullOrWhiteSpace(n.FontFamily)) n.FontFamily = "Segoe UI";
            if (!TryParseColor(n.Color, out byte r, out byte g, out byte b)) { r = g = b = 255; }
            n.Color = $"#{r:X2}{g:X2}{b:X2}";
            n.SizePercent = Clamp(n.SizePercent, 1, 100, 6);
            n.OpacityPercent = Clamp(n.OpacityPercent, 1, 100, 60);
            n.Rotation = Clamp(n.Rotation, -180, 180, 0);
            n.MarginPercent = Clamp(n.MarginPercent, 0, 30, 3);
            n.TileGapPercent = Clamp(n.TileGapPercent, 0, 400, 120);
            if (n.LogoFile != null && (n.LogoFile.Contains('\\') || n.LogoFile.Contains('/') || n.LogoFile.Contains(':'))) n.LogoFile = null;   // only names of our own copies
            return n;

            static double Clamp(double v, double min, double max, double fallback) => double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
        }

        /// <summary>True when there is something to draw: a non-empty text, or a logo.</summary>
        public static bool HasMark(WatermarkSettings s) =>
            s.Kind == WatermarkKind.Text ? !string.IsNullOrWhiteSpace(s.Text) : !string.IsNullOrEmpty(s.LogoFile) && File.Exists(LogoPath(s.LogoFile));

        // =================================================================
        //  Presets
        // =================================================================

        public static IReadOnlyList<WatermarkPreset> BuiltInPresets { get; } = new[]
        {
            new WatermarkPreset
            {
                Name = "Draft", BuiltIn = true,
                Settings = new WatermarkSettings { Text = "DRAFT", Color = "#E53935", Outline = false, SizePercent = 16, OpacityPercent = 28, Rotation = -30, Placement = WatermarkPlacement.Center },
            },
            new WatermarkPreset
            {
                Name = "Confidential", BuiltIn = true,
                Settings = new WatermarkSettings { Text = "CONFIDENTIAL", Color = "#757575", Outline = false, SizePercent = 5, OpacityPercent = 24, Rotation = -30, Placement = WatermarkPlacement.Tiled, TileGapPercent = 160 },
            },
            new WatermarkPreset
            {
                Name = "Copyright", BuiltIn = true,
                Settings = new WatermarkSettings { Text = "© Your name", Color = "#FFFFFF", Outline = true, SizePercent = 4, OpacityPercent = 70, Placement = WatermarkPlacement.BottomRight, MarginPercent = 3 },
            },
            new WatermarkPreset
            {
                Name = "Company logo", BuiltIn = true,
                Settings = new WatermarkSettings { Kind = WatermarkKind.Image, SizePercent = 14, OpacityPercent = 80, Placement = WatermarkPlacement.BottomRight, MarginPercent = 3 },
            },
        };

        /// <summary>A usable preset name: trimmed, at most 40 characters, not one of the built-in names.</summary>
        public static string? CleanPresetName(string? name, IEnumerable<WatermarkPreset> existing, out bool replacesExisting)
        {
            replacesExisting = false;
            string n = (name ?? "").Trim();
            if (n.Length == 0) return null;
            if (n.Length > 40) n = n[..40].TrimEnd();
            var clash = existing.FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase));
            if (clash is { BuiltIn: true }) return null;
            replacesExisting = clash != null;
            return n;
        }

        // =================================================================
        //  Logos: the app keeps its own copy, so a preset keeps working when the original moves
        // =================================================================

        public static string LogoFolder => Path.Combine(AppPaths.DataRoot, "Watermark logos");

        public static string LogoPath(string logoFile) => Path.Combine(LogoFolder, Path.GetFileName(logoFile));

        /// <summary>Copies a logo into the app's own folder and returns the name of the copy (null when it can't be copied).</summary>
        public static string? ImportLogo(string sourcePath, string? folder = null)
        {
            try
            {
                string target = folder ?? LogoFolder;
                Directory.CreateDirectory(target);
                string name = "logo-" + Guid.NewGuid().ToString("N")[..10] + Path.GetExtension(sourcePath).ToLowerInvariant();
                File.Copy(sourcePath, Path.Combine(target, name), overwrite: false);
                return name;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>Removes copies that no preset or current setup uses any more.</summary>
        public static void DeleteUnusedLogos(IEnumerable<string?> inUse, string? folder = null)
        {
            try
            {
                string dir = folder ?? LogoFolder;
                if (!Directory.Exists(dir)) return;
                var keep = new HashSet<string>(inUse.Where(n => !string.IsNullOrEmpty(n)).Select(n => Path.GetFileName(n!)), StringComparer.OrdinalIgnoreCase);
                foreach (string file in Directory.EnumerateFiles(dir, "logo-*"))
                    if (!keep.Contains(Path.GetFileName(file))) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a leftover copy is harmless
            }
        }

        // =================================================================
        //  Settings and presets files (%AppData%\Smart Tools\watermark*.json - the setup only, never files or folders)
        // =================================================================

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        private static string AppFolder => AppPaths.DataRoot;

        public static WatermarkViewSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? Path.Combine(AppFolder, "watermark.json");
                if (!File.Exists(file)) return new WatermarkViewSettings();
                var s = JsonSerializer.Deserialize<WatermarkViewSettings>(File.ReadAllText(file), Json) ?? new WatermarkViewSettings();
                s.Mark = Normalize(s.Mark ?? new WatermarkSettings());
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new WatermarkViewSettings();
            }
        }

        public static void SaveSettings(WatermarkViewSettings settings, string? path = null) =>
            WriteJson(path ?? Path.Combine(AppFolder, "watermark.json"), settings);

        /// <summary>The user's own presets (the built-in ones are in code).</summary>
        public static List<WatermarkPreset> LoadPresets(string? path = null)
        {
            try
            {
                string file = path ?? Path.Combine(AppFolder, "watermark_presets.json");
                if (!File.Exists(file)) return new List<WatermarkPreset>();
                var list = JsonSerializer.Deserialize<List<WatermarkPreset>>(File.ReadAllText(file), Json) ?? new List<WatermarkPreset>();
                var clean = new List<WatermarkPreset>();
                foreach (var p in list)
                {
                    string? name = CleanPresetName(p.Name, BuiltInPresets.Concat(clean), out bool replaces);
                    if (name == null || replaces) continue;               // blank, a built-in name or a duplicate
                    clean.Add(new WatermarkPreset { Name = name, Settings = Normalize(p.Settings ?? new WatermarkSettings()) });
                }
                return clean;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new List<WatermarkPreset>();
            }
        }

        public static void SavePresets(IEnumerable<WatermarkPreset> presets, string? path = null) =>
            WriteJson(path ?? Path.Combine(AppFolder, "watermark_presets.json"), presets.Where(p => !p.BuiltIn).ToList());

        private static void WriteJson<T>(string file, T value)
        {
            string temp = file + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
