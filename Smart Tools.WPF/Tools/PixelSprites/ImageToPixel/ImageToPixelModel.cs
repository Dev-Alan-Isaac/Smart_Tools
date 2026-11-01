using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;

namespace Smart_Tools.WPF.Models
{
    public enum PixelItemStatus { Pending, Working, Done, Failed }

    /// <summary>A file or folder added on the Input card.</summary>
    public sealed class PixelSource
    {
        public required string FullPath { get; init; }
        public bool IsFolder { get; init; }
        public int Count { get; set; }

        public string Name => Path.GetFileName(FullPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : FullPath;
        public string Subtitle => IsFolder ? $"Folder · {Count} picture{(Count == 1 ? "" : "s")}" : "Picture";
    }

    /// <summary>One picture in the list: what it is, and how its conversion went.</summary>
    public sealed class PixelItem : INotifyPropertyChanged
    {
        private PixelItemStatus _status;
        private string _message = "";
        private BitmapSource? _thumbnail;

        public required string FullPath { get; init; }
        public string Name => Path.GetFileName(FullPath);

        public PixelItemStatus Status
        {
            get => _status;
            set { if (_status != value) { _status = value; OnChanged(); OnChanged(nameof(StatusText)); } }
        }

        public string Message
        {
            get => _message;
            set { if (_message != value) { _message = value; OnChanged(); OnChanged(nameof(StatusText)); } }
        }

        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; OnChanged(); }
        }

        public string StatusText => _status switch
        {
            PixelItemStatus.Working => "Working...",
            PixelItemStatus.Done => "Saved " + _message,
            PixelItemStatus.Failed => _message,
            _ => "",
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
