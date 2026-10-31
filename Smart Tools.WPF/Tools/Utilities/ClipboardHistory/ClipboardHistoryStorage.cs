using Smart_Tools.WPF.Models;
using Smart_Tools.WPF.Modules;
using System.IO;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The optional saved copy of the Clipboard History ("keep history between sessions"). The file is encrypted with
    /// the Windows account (DPAPI), so other users and other PCs can't read it (programs run as the same user can).
    /// Unpinned pictures over <see cref="MaxSavedImageBytes"/> are not saved, to keep the file small; pinned ones are,
    /// until <see cref="MaxSavedImageTotalBytes"/> is used up. The DPAPI half is in <c>ClipboardHistoryStorage.Dpapi.cs</c>.
    /// Not thread-safe by itself: <see cref="ClipboardHistoryService"/> serializes every save and delete.
    /// </summary>
    public static partial class ClipboardHistoryStorage
    {
        // The settings text says "5 MB": keep it in step with this value.
        private const int MaxSavedImageBytes = 5 * 1024 * 1024;
        private const long MaxSavedImageTotalBytes = 64L * 1024 * 1024;

        private static readonly string FilePath = Path.Combine(AppSettingsStore.SettingsDirectory, "clipboard-history.dat");

        /// <summary>Saves <paramref name="items"/>, given in list order (pinned first, then newest) so pinned pictures get the size budget first.</summary>
        public static void Save(IEnumerable<ClipboardItem> items)
        {
            try
            {
                var stored = new List<ClipboardItem>();
                long budget = MaxSavedImageTotalBytes;
                foreach (var item in items)
                {
                    if (item.IsImage)
                    {
                        if (item.ImagePng is not { Length: > 0 } png || png.Length > budget || (!item.IsPinned && png.Length > MaxSavedImageBytes)) continue;
                        budget -= png.Length;
                    }
                    stored.Add(item);
                }
                AtomicFile.WriteAllBytes(FilePath, Protect(JsonSerializer.SerializeToUtf8Bytes(stored)));
            }
            catch (Exception ex)
            {
                Log.Warn("Couldn't save the clipboard history", ex);
            }
        }

        /// <summary>The saved items as written (the manager decides which are fit to restore), or an empty list when there is none or it can't be read.</summary>
        public static List<ClipboardItem> Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new();
                return (JsonSerializer.Deserialize<List<ClipboardItem?>>(Unprotect(File.ReadAllBytes(FilePath))) ?? new())
                    .OfType<ClipboardItem>()
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Warn("Couldn't read the saved clipboard history", ex);
                return new();
            }
        }

        public static void Delete()
        {
            try
            {
                File.Delete(FilePath);
            }
            catch (Exception ex)
            {
                Log.Warn("Couldn't delete the saved clipboard history", ex);
            }
        }
    }
}
