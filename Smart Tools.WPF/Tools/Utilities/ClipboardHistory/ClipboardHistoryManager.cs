using Smart_Tools.WPF.Models;
using System.Security.Cryptography;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Clipboard History - the list of what was copied. Memory only unless "keep history between sessions" is on
    /// (see <see cref="ClipboardHistoryStorage"/>); otherwise everything is dropped when Smart Tools closes. Copies that are marked private (the Password Vault marks its
    /// own) are never recorded. This half is portable (no WPF); the listener and the clipboard are in
    /// <c>ClipboardHistoryManager.Windows.cs</c>.
    /// </summary>
    public sealed partial class ClipboardHistoryManager : IDisposable
    {
        public const int MaxItems = 100;
        public const int MaxImages = 20;
        public const int MaxTextChars = 1_000_000;
        public const int MaxImageBytes = 25 * 1024 * 1024;

        private readonly List<ClipboardItem> _items = new();

        /// <summary>Raised after the list changed (always on the UI thread).</summary>
        public event Action? Changed;

        /// <summary>
        /// The user's pause button in the tool: false = copies are ignored until it is turned back on. Separate from
        /// <c>Start</c>/<c>Stop</c>, which is whether the clipboard listener exists at all (decided by the service).
        /// </summary>
        public bool IsRecording { get; set; } = true;

        public int Count => _items.Count;

        /// <summary>Pinned items first, then the newest. <paramref name="query"/> filters text items (pictures match nothing).</summary>
        public List<ClipboardItem> Snapshot(string? query = null)
        {
            string q = query?.Trim() ?? "";
            return _items
                .Where(i => q.Length == 0 || (!i.IsImage && i.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
                .OrderByDescending(i => i.IsPinned)
                .ThenByDescending(i => i.CopiedAt)
                .ToList();
        }

        public ClipboardItem? AddText(string text, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextChars) return null;
            return Add(new ClipboardItem { Kind = ClipboardItemKind.Text, Text = text, Key = "T" + Hash(Encoding.UTF8.GetBytes(text)), CopiedAt = now });
        }

        public ClipboardItem? AddImage(byte[] png, int width, int height, DateTime now)
        {
            if (png.Length == 0 || png.Length > MaxImageBytes) return null;
            return Add(new ClipboardItem { Kind = ClipboardItemKind.Image, ImagePng = png, ImageWidth = width, ImageHeight = height, Key = "I" + Hash(png), CopiedAt = now });
        }

        /// <summary>
        /// Puts back items saved earlier (no <see cref="Changed"/>: nothing new was copied). Items already in the list
        /// win, and anything that wouldn't be accepted when copied today (empty, over the size limits) is skipped.
        /// </summary>
        public void Restore(IEnumerable<ClipboardItem> saved)
        {
            foreach (var item in saved)
                if (IsRestorable(item) && _items.All(i => i.Key != item.Key)) _items.Add(item);
            Trim();
        }

        // A saved file is outside input: its strings can be null even though the type says they can't.
        private static bool IsRestorable(ClipboardItem item) => !string.IsNullOrEmpty(item.Key) && (item.IsImage
            ? item.ImagePng is { Length: > 0 and <= MaxImageBytes }
            : !string.IsNullOrWhiteSpace(item.Text) && item.Text.Length <= MaxTextChars);

        /// <summary>Removes everything, pinned items too.</summary>
        public void ClearAll()
        {
            if (_items.Count == 0) return;
            _items.Clear();
            Changed?.Invoke();
        }

        private ClipboardItem Add(ClipboardItem item)
        {
            var existing = _items.FirstOrDefault(i => i.Key == item.Key);
            if (existing != null)
            {
                existing.CopiedAt = item.CopiedAt;   // copied again: back to the top, keeps its pin
                Changed?.Invoke();
                return existing;
            }

            _items.Add(item);
            Trim();
            Changed?.Invoke();
            return item;
        }

        /// <summary>Drops the oldest unpinned items beyond the limits. Pinned items are never dropped.</summary>
        private void Trim()
        {
            Drop(_items.Count - MaxItems, _ => true);
            Drop(_items.Count(i => i.IsImage) - MaxImages, i => i.IsImage);

            void Drop(int surplus, Func<ClipboardItem, bool> match)
            {
                if (surplus <= 0) return;
                var oldest = _items.Where(i => !i.IsPinned && match(i)).OrderBy(i => i.CopiedAt).Take(surplus).ToList();
                foreach (var old in oldest) _items.Remove(old);
            }
        }

        public void TogglePin(ClipboardItem item)
        {
            item.IsPinned = !item.IsPinned;
            Changed?.Invoke();
        }

        public void Remove(ClipboardItem item)
        {
            if (_items.Remove(item)) Changed?.Invoke();
        }

        /// <summary>Removes everything that isn't pinned.</summary>
        public int ClearUnpinned()
        {
            int removed = _items.RemoveAll(i => !i.IsPinned);
            if (removed > 0) Changed?.Invoke();
            return removed;
        }

        private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    }
}
