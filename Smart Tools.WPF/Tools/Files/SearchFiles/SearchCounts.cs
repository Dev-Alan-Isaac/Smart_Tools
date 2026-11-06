namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Counted nouns for the search tool ("1 match", "3 matches"), in the current language.</summary>
    internal static class SearchCounts
    {
        private static int C(long n) => (int)Math.Min(int.MaxValue, Math.Max(0, n));

        public static string Matches(long n) => Loc.N(C(n), "Str_SearchFiles_Match1", "{0:N0} match", "Str_SearchFiles_MatchN", "{0:N0} matches", n);
        public static string Values(long n) => Loc.N(C(n), "Str_SearchFiles_Value1", "{0:N0} value", "Str_SearchFiles_ValueN", "{0:N0} values", n);
        public static string DiffValues(long n) => Loc.N(C(n), "Str_SearchFiles_DiffValue1", "{0:N0} different value", "Str_SearchFiles_DiffValueN", "{0:N0} different values", n);
        public static string Files(long n) => Loc.N(C(n), "Str_Files_File1", "{0:N0} file", "Str_Files_FileN", "{0:N0} files", n);
        public static string Rows(long n) => Loc.N(C(n), "Str_SearchFiles_Row1", "{0:N0} row", "Str_SearchFiles_RowN", "{0:N0} rows", n);
        public static string Patterns(long n) => Loc.N(C(n), "Str_SearchFiles_Pattern1", "{0:N0} pattern ticked", "Str_SearchFiles_PatternN", "{0:N0} patterns ticked", n);
    }
}
