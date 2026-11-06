namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Counted nouns shared by the Files tools ("1 file", "3 files"), in the current language.</summary>
    internal static class FilesText
    {
        public static string Files(int n) => Loc.N(n, "Str_Files_File1", "{0:N0} file", "Str_Files_FileN", "{0:N0} files", n);
        public static string Folders(int n) => Loc.N(n, "Str_Files_Folder1", "{0:N0} folder", "Str_Files_FolderN", "{0:N0} folders", n);
    }
}
