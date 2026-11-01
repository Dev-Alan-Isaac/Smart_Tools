using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;

namespace Smart_Tools.WPF.Models
{
    /// <summary>How a texture row stands: fine, worth a look, or left out of the pack.</summary>
    public enum PackTextureState { Ok, Warning, Error }

    /// <summary>One Minecraft version (or range) and the number its pack.mcmeta needs.</summary>
    public sealed record PackVersion(string Label, int Format, bool UsesMinMax)
    {
        public override string ToString() => Label;
    }

    /// <summary>A file or folder the user added (the card's list; the pictures themselves are in the texture list).</summary>
    public sealed class TextureSource
    {
        public required string FullPath { get; init; }
        public bool IsFolder { get; init; }
        public int Count { get; set; }

        public string Name => Path.GetFileName(FullPath.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : FullPath;
        public string Subtitle => IsFolder ? $"Folder · {Count} PNG" : "PNG file";
    }

    /// <summary>One picture and the Minecraft texture it will replace (for example "block/stone").</summary>
    public sealed class PackTexture : INotifyPropertyChanged
    {
        private string _slot = "";
        private bool _isChecked = true;
        private BitmapSource? _thumbnail;
        private int _width, _height;
        private PackTextureState _state;
        private string _message = "";

        public required string FullPath { get; init; }
        public string FileName => Path.GetFileName(FullPath);

        /// <summary>Where it goes inside assets/minecraft/textures, without ".png". Editable in the list.</summary>
        public string Slot
        {
            get => _slot;
            set { if (_slot != value) { _slot = value; OnChanged(); } }
        }

        /// <summary>False when the matching was a guess the user hasn't confirmed - kept for the tooltip only.</summary>
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value; OnChanged(); } }
        }

        public int Width { get => _width; set { _width = value; OnChanged(); OnChanged(nameof(SizeText)); } }
        public int Height { get => _height; set { _height = value; OnChanged(); OnChanged(nameof(SizeText)); } }
        public string SizeText => _width > 0 ? $"{_width} × {_height}" : "";

        public BitmapSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; OnChanged(); } }

        public PackTextureState State
        {
            get => _state;
            set { if (_state != value) { _state = value; OnChanged(); OnChanged(nameof(ShowIcon)); } }
        }

        public string Message { get => _message; set { if (_message != value) { _message = value; OnChanged(); } } }
        public bool ShowIcon => _state != PackTextureState.Ok;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>The setup that is remembered (never the pictures or their paths).</summary>
    public sealed class TexturePackSettings
    {
        public string PackName { get; set; } = "My Texture Pack";
        public string Description { get; set; } = "Made with Smart Tools";
        public string VersionLabel { get; set; } = "";
        public bool IncludeSubfolders { get; set; } = true;
        public bool AsZip { get; set; } = true;
    }

    /// <summary>What an export did.</summary>
    public sealed record TexturePackResult(string? Path, int Written, int Skipped, string? Problem);
}
