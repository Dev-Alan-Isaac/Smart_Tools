using Microsoft.VisualBasic.FileIO;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Removing the file behind a recording or a changed voice from the disk (to the Recycle Bin, so it can be brought back).</summary>
    internal static class TakeFiles
    {
        /// <summary>True when the file is gone (or was already gone); false with the reason when Windows refused.</summary>
        public static bool Delete(string path, out string? error)
        {
            error = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return true;
            try
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
