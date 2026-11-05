using Smart_Tools.WPF.Models;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Minecraft Java Edition resource pack builder - logic only (no WPF), the pictures' thumbnails come from
    /// <see cref="SpritePackerManager"/>. A pack is pack.mcmeta + optional pack.png + assets/minecraft/textures/&lt;slot&gt;.png.
    /// Files are copied as they are (never re-encoded), and an export never replaces an earlier one.
    /// </summary>
    public static partial class TexturePackManager
    {
        public const int MaxTextures = 5000;
        public const long MaxFileBytes = 64L * 1024 * 1024;

        private static readonly JsonSerializerOptions SettingsJson = new() { WriteIndented = true };

        // =================================================================
        //  Finding pictures and guessing their slot
        // =================================================================

        /// <summary>Every .png under the paths (a folder, or a loose file), once each, A-Z. Links aren't followed.</summary>
        public static (List<string> Files, int Inaccessible) FindPngs(IEnumerable<string> paths, bool includeSubfolders)
        {
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int inaccessible = 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = includeSubfolders,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (string path in paths)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        var here = Directory.EnumerateFiles(path, "*.png", options).ToList();
                        here.Sort(StringComparer.OrdinalIgnoreCase);
                        foreach (string f in here) if (seen.Add(f)) files.Add(f);
                    }
                    else if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
                    {
                        if (seen.Add(path)) files.Add(path);
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    inaccessible++;
                }
            }
            return (files, inaccessible);
        }

        /// <summary>
        /// The slot for a file: the folder it sits in when that is a texture folder (block/stone.png), a "block_" style
        /// prefix (block_stone.png), or the name in the vanilla list. Empty when nothing fits.
        /// </summary>
        public static string GuessSlot(string path)
        {
            string stem = NormalizeName(Path.GetFileNameWithoutExtension(path));
            if (stem.Length == 0) return "";

            string parent = NormalizeName(Path.GetFileName(Path.GetDirectoryName(path) ?? ""));
            if (TextureFolders.Contains(parent)) return parent + "/" + stem;

            int cut = stem.IndexOf('_');
            if (cut > 0 && TextureFolders.Contains(stem[..cut]) && cut < stem.Length - 1)
                return stem[..cut] + "/" + stem[(cut + 1)..];

            return Known.TryGetValue(stem, out string? slot) ? slot : "";
        }

        /// <summary>Lowercase, spaces and dashes become "_" - how Minecraft names its textures.</summary>
        public static string NormalizeName(string? name) =>
            (name ?? "").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

        /// <summary>A typed slot as a clean "folder/name": slashes forward, no ".png", no "assets/minecraft/textures/" in front.</summary>
        public static string NormalizeSlot(string? slot)
        {
            string s = (slot ?? "").Trim().Replace('\\', '/').ToLowerInvariant().Replace(' ', '_');
            const string prefix = "assets/minecraft/textures/";
            if (s.StartsWith(prefix, StringComparison.Ordinal)) s = s[prefix.Length..];
            if (s.EndsWith(".png", StringComparison.Ordinal)) s = s[..^4];
            return s.Trim('/');
        }

        /// <summary>Null when Minecraft can use the slot; otherwise why not.</summary>
        public static string? CheckSlot(string slot)
        {
            if (slot.Length == 0) return Loc.T("Str_TexturePack_MsgSlotPick", "Pick where this texture goes (for example block/stone).");
            if (!slot.Contains('/')) return Loc.T("Str_TexturePack_MsgSlotFolder", "Use folder/name, for example block/stone.");
            if (slot.Contains("//") || slot.Split('/').Any(part => part is "" or "." or ".."))
                return Loc.T("Str_TexturePack_MsgSlotEmptyPart", "That path has an empty or invalid part.");
            foreach (char c in slot)
                if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.' or '/'))
                    return Loc.T("Str_TexturePack_MsgSlotChars", "Only lowercase letters, numbers and _ - . / are allowed.");
            return null;
        }

        public static bool IsKnownSlot(string slot) =>
            slot.IndexOf('/') is int i && i > 0 && Known.TryGetValue(slot[(i + 1)..], out string? known) && known == slot;

        /// <summary>
        /// Sets each row's state: unreadable or unnamed rows are errors (left out), odd sizes and unknown names are
        /// warnings (still exported), and when two checked rows share a slot only the first is exported.
        /// </summary>
        public static void Validate(IReadOnlyList<PackTexture> textures)
        {
            var used = new Dictionary<string, PackTexture>(StringComparer.Ordinal);
            foreach (var t in textures)
            {
                string slot = NormalizeSlot(t.Slot);
                t.State = PackTextureState.Ok;
                t.Message = "";

                if (!t.IsChecked) { t.Message = Loc.T("Str_TexturePack_MsgLeftOutOfPack", "Left out of the pack."); continue; }
                if (t.Width <= 0 || t.Height <= 0) { Set(t, PackTextureState.Error, Loc.T("Str_TexturePack_MsgUnreadable", "This picture couldn't be read, so it won't be exported.")); continue; }
                string? bad = CheckSlot(slot);
                if (bad != null) { Set(t, PackTextureState.Error, Loc.F("Str_TexturePack_MsgUntilYouDo", "{0} It won't be exported until you do.", bad)); continue; }
                if (used.TryGetValue(slot, out var first))
                {
                    Set(t, PackTextureState.Error, Loc.F("Str_TexturePack_MsgDuplicateSlot", "{0} already goes to {1}; this one won't be exported.", first.FileName, slot));
                    continue;
                }
                used[slot] = t;

                var notes = new List<string>();
                if (!IsPowerOfTwo(t.Width))
                    notes.Add(Loc.F("Str_TexturePack_MsgNotPowerOfTwo", "{0} px wide isn't a power of two (16, 32, 64...).", t.Width));
                if (t.Height != t.Width && t.Height % t.Width != 0)
                    notes.Add(Loc.T("Str_TexturePack_MsgNotSquare", "It isn't square (only animated strips may be taller)."));
                if (!IsKnownSlot(slot) && (slot.StartsWith("block/", StringComparison.Ordinal) || slot.StartsWith("item/", StringComparison.Ordinal)))
                    notes.Add(Loc.T("Str_TexturePack_MsgUnknownName", "Not in the built-in name list - fine for names we don't have, but Minecraft ignores names it doesn't use."));
                else if (!slot.StartsWith("block/", StringComparison.Ordinal) && !slot.StartsWith("item/", StringComparison.Ordinal) && !TextureFolders.Contains(slot[..slot.IndexOf('/')]))
                    notes.Add(Loc.T("Str_TexturePack_MsgUnusualFolder", "Not a usual texture folder."));
                if (notes.Count > 0) Set(t, PackTextureState.Warning, string.Join(" ", notes));
            }

            static void Set(PackTexture t, PackTextureState state, string message)
            {
                t.State = state;
                t.Message = message;
            }
        }

        private static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

        /// <summary>The checked rows that will really be written (valid, first of each slot).</summary>
        public static List<PackTexture> Exportable(IEnumerable<PackTexture> textures) =>
            textures.Where(t => t.IsChecked && t.State != PackTextureState.Error).ToList();

        // =================================================================
        //  pack.mcmeta
        // =================================================================

        /// <summary>pack.mcmeta text. 1.21.8 and older use pack_format; 1.21.9 and newer use min_format / max_format.</summary>
        public static string BuildMcmeta(string description, PackVersion version)
        {
            var pack = new Dictionary<string, object>();
            if (version.UsesMinMax)
            {
                pack["min_format"] = version.Format;
                pack["max_format"] = version.Format;
            }
            else
            {
                pack["pack_format"] = version.Format;
            }
            pack["description"] = description ?? "";
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            return JsonSerializer.Serialize(new Dictionary<string, object> { ["pack"] = pack }, options);
        }

        // =================================================================
        //  Saving
        // =================================================================

        /// <summary>
        /// Writes the pack. As a zip: <paramref name="path"/> is the .zip (built beside it and moved into place, so a
        /// failure leaves nothing half-written). As a folder: the same name without ".zip", numbered if taken.
        /// </summary>
        public static TexturePackResult Save(string path, bool overwriteAllowed, bool asZip, string description, PackVersion version,
            IReadOnlyList<PackTexture> textures, string? iconPath, CancellationToken token = default)
        {
            var items = Exportable(textures);
            if (items.Count == 0 && iconPath == null) return new TexturePackResult(null, 0, 0, Loc.T("Str_TexturePack_MsgNothingToPut", "There is nothing to put in the pack yet."));
            int skipped = textures.Count - items.Count;
            string mcmeta = BuildMcmeta(description, version);
            string textureRoot = "assets/minecraft/textures/";

            try
            {
                string full = Path.GetFullPath(path);
                string parent = Path.GetDirectoryName(full)!;
                Directory.CreateDirectory(parent);

                if (asZip)
                {
                    string target = overwriteAllowed || !File.Exists(full) ? full : UniqueFile(full);
                    string temp = Path.Combine(parent, $".{Guid.NewGuid():N}.tmp");
                    try
                    {
                        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                        {
                            WriteText(zip, "pack.mcmeta", mcmeta);
                            if (iconPath != null) zip.CreateEntryFromFile(iconPath, "pack.png", CompressionLevel.Optimal);
                            foreach (var t in items)
                            {
                                token.ThrowIfCancellationRequested();
                                zip.CreateEntryFromFile(t.FullPath, textureRoot + NormalizeSlot(t.Slot) + ".png", CompressionLevel.Optimal);
                            }
                        }
                        File.Move(temp, target, overwrite: true);
                    }
                    finally
                    {
                        try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    }
                    return new TexturePackResult(target, items.Count, skipped, null);
                }

                string folder = SpritePackerManager.UniqueFolder(Path.Combine(parent, Path.GetFileNameWithoutExtension(full)));
                try
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "pack.mcmeta"), mcmeta, new UTF8Encoding(false));
                    if (iconPath != null) File.Copy(iconPath, Path.Combine(folder, "pack.png"));
                    foreach (var t in items)
                    {
                        token.ThrowIfCancellationRequested();
                        string dest = Path.Combine(folder, textureRoot.Replace('/', Path.DirectorySeparatorChar), NormalizeSlot(t.Slot).Replace('/', Path.DirectorySeparatorChar) + ".png");
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(t.FullPath, dest, overwrite: false);
                    }
                }
                catch
                {
                    try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    throw;
                }
                return new TexturePackResult(folder, items.Count, skipped, null);
            }
            catch (OperationCanceledException)
            {
                return new TexturePackResult(null, 0, skipped, Loc.T("Str_TexturePack_MsgStopped", "Stopped."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException)
            {
                return new TexturePackResult(null, 0, skipped, ex.Message);
            }
        }

        private static void WriteText(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(text);
        }

        private static string UniqueFile(string path)
        {
            string folder = Path.GetDirectoryName(path)!;
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(folder, $"{name} ({n}){ext}");
                if (!File.Exists(candidate)) return candidate;
            }
        }

        // =================================================================
        //  Settings (%AppData%\Smart Tools\texture_pack.json - the setup only, never a picture or a path)
        // =================================================================

        private static string SettingsPath => Path.Combine(AppPaths.DataRoot, "texture_pack.json");

        public static TexturePackSettings LoadSettings(string? path = null)
        {
            try
            {
                string file = path ?? SettingsPath;
                if (!File.Exists(file)) return new TexturePackSettings();
                var s = JsonSerializer.Deserialize<TexturePackSettings>(File.ReadAllText(file), SettingsJson) ?? new TexturePackSettings();
                s.PackName = (s.PackName ?? "").Length > 100 ? s.PackName![..100] : s.PackName ?? "";
                s.Description = (s.Description ?? "").Length > 300 ? s.Description![..300] : s.Description ?? "";
                s.VersionLabel ??= "";
                return s;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return new TexturePackSettings();
            }
        }

        public static void SaveSettings(TexturePackSettings settings, string? path = null)
        {
            string file = path ?? SettingsPath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                AtomicFile.WriteAllText(file, JsonSerializer.Serialize(settings, SettingsJson));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Info($"Settings not saved: {ex.Message}");
            }
        }
    }
}
