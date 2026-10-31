using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Smart_Tools.WPF.Models
{
    public enum ClipboardItemKind { Text, Image }

    /// <summary>One thing that was copied. Kept in memory; written to disk (encrypted) only when "keep history between sessions" is on. Saved as-is: the computed and view-only members are [JsonIgnore].</summary>
    public sealed class ClipboardItem : INotifyPropertyChanged
    {
        private bool _isPinned;
        private DateTime _copiedAt;
        private string _metaText = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        [JsonIgnore]
        public Guid Id { get; } = Guid.NewGuid();

        public ClipboardItemKind Kind { get; init; }

        /// <summary>Same content = same key, so copying something twice moves it up instead of adding a second row.</summary>
        public string Key { get; init; } = "";

        public string Text { get; init; } = "";

        /// <summary>The copied picture as PNG bytes (Image items only).</summary>
        public byte[]? ImagePng { get; init; }

        public int ImageWidth { get; init; }

        public int ImageHeight { get; init; }

        [JsonIgnore]
        public bool IsImage => Kind == ClipboardItemKind.Image;

        /// <summary>The text on one line, shortened, for the list. For a picture: its size.</summary>
        [JsonIgnore]
        public string Preview => IsImage ? $"{ImageWidth} × {ImageHeight}" : OneLine(Text, 200);

        public DateTime CopiedAt
        {
            get => _copiedAt;
            set
            {
                if (_copiedAt == value) return;
                _copiedAt = value;
                Raise(nameof(CopiedAt));
            }
        }

        public bool IsPinned
        {
            get => _isPinned;
            set
            {
                if (_isPinned == value) return;
                _isPinned = value;
                Raise(nameof(IsPinned));
            }
        }

        /// <summary>The small grey line under the preview (time and size). Filled in by the view, which knows the language.</summary>
        [JsonIgnore]
        public string MetaText
        {
            get => _metaText;
            set
            {
                if (_metaText == value) return;
                _metaText = value;
                Raise(nameof(MetaText));
            }
        }

        private static string OneLine(string text, int max)
        {
            var parts = text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string line = string.Join(' ', parts.Select(p => p.Trim()).Where(p => p.Length > 0));
            return line.Length <= max ? line : line[..max] + "…";
        }

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
