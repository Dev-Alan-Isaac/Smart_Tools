namespace Smart_Tools.WPF.Controllers
{
    /// <summary>The one "1.5 MB" formatter. Tool text helpers forward here instead of keeping their own copy.</summary>
    public static class ByteFormat
    {
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

        /// <summary>"512 B", "4 KB", "1.5 MB" (one decimal, trailing zero dropped; bytes stay whole).</summary>
        public static string Format(long bytes)
        {
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < Units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return $"{size:0.#} {Units[unit]}";
        }
    }
}
