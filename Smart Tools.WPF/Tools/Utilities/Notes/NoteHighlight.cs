using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Attached properties for a TextBlock that shows text with the search words highlighted:
    /// bind <c>Text</c> to the text and <c>Query</c> to the search box's content. Used by the note cards.
    /// </summary>
    public static class NoteHighlight
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached("Text", typeof(string), typeof(NoteHighlight), new PropertyMetadata("", (d, _) => Rebuild(d)));

        public static readonly DependencyProperty QueryProperty =
            DependencyProperty.RegisterAttached("Query", typeof(string), typeof(NoteHighlight), new PropertyMetadata("", (d, _) => Rebuild(d)));

        public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
        public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);
        public static string GetQuery(DependencyObject d) => (string)d.GetValue(QueryProperty);
        public static void SetQuery(DependencyObject d, string value) => d.SetValue(QueryProperty, value);

        private static void Rebuild(DependencyObject d)
        {
            if (d is not TextBlock block) return;
            string text = GetText(d) ?? "";
            var matches = NotesManager.FindMatches(text, GetQuery(d));

            block.Inlines.Clear();
            if (matches.Count == 0)
            {
                block.Inlines.Add(new Run(text));
                return;
            }

            int at = 0;
            foreach (var (start, length) in matches)
            {
                if (start > at) block.Inlines.Add(new Run(text[at..start]));
                var hit = new Run(text.Substring(start, length)) { FontWeight = FontWeights.Bold };
                hit.SetResourceReference(TextElement.BackgroundProperty, "AccentFill");
                hit.Foreground = System.Windows.Media.Brushes.White;
                block.Inlines.Add(hit);
                at = start + length;
            }
            if (at < text.Length) block.Inlines.Add(new Run(text[at..]));
        }
    }
}
