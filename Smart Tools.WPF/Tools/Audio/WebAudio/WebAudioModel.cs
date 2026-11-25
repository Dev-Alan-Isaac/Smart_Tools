using System.IO;
using Smart_Tools.WPF.Controllers;

namespace Smart_Tools.WPF.Models
{
    /// <summary>One saved sound in the result list.</summary>
    public sealed class WebAudioRow
    {
        public WebAudioRow(string path)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
            Folder = System.IO.Path.GetDirectoryName(path) ?? "";
            SizeText = SafeIo.TryGetLength(path, out long length) ? OutputFolderManager.FormatBytes(length) : "";
        }

        public string Path { get; }

        public string Name { get; }

        public string Folder { get; }

        public string SizeText { get; }
    }
}
