using Smart_Tools.WPF.Controllers;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using Image = System.Windows.Controls.Image;
using RichTextBox = System.Windows.Controls.RichTextBox;
using MenuItem = System.Windows.Controls.MenuItem;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;

namespace Smart_Tools.WPF.Views
{
    /// <summary>What is remembered about a picture inside a note: its file in the notes pictures folder, its size and where it came from.</summary>
    internal sealed record NoteImageInfo(string File, double Width, double Height, string Source);

    /// <summary>
    /// Turns a note's rich text to and from a FlowDocument. A note is stored as a small HTML-like string
    /// (&lt;p&gt;, &lt;ul&gt;&lt;li&gt;, &lt;b&gt; &lt;i&gt; &lt;u&gt; &lt;s&gt;, &lt;br/&gt;, &lt;img f w h s/&gt;), so the file stays readable and the
    /// pictures live as PNG files beside it, not inside the JSON. Notes that have no rich text yet load from their plain text.
    /// </summary>
    internal static class NoteRichText
    {
        public const double MaxImageWidth = 420;

        private readonly record struct Fmt(bool Bold, bool Italic, bool Underline, bool Strike);

        // =================================================================
        //  Text -> document
        // =================================================================

        public static FlowDocument Build(string? rich, string? plain)
        {
            var doc = NewDocument();
            if (!string.IsNullOrWhiteSpace(rich) && TryParse(rich, doc)) return doc;

            foreach (string line in (plain ?? "").Replace("\r\n", "\n").Split('\n'))
                doc.Blocks.Add(new Paragraph(new Run(line)) { Margin = new Thickness(0, 0, 0, 4) });
            return doc;
        }

        private static FlowDocument NewDocument() => new() { PagePadding = new Thickness(0) };

        private static bool TryParse(string rich, FlowDocument doc)
        {
            XElement root;
            try
            {
                root = XElement.Parse("<n>" + rich + "</n>", LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                return false;
            }

            foreach (var element in root.Elements())
            {
                switch (element.Name.LocalName)
                {
                    case "ul":
                        var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(22, 0, 0, 0) };
                        foreach (var li in element.Elements("li"))
                        {
                            var para = new Paragraph { Margin = new Thickness(0) };
                            AddInlines(para.Inlines, li, default);
                            var item = new ListItem();
                            item.Blocks.Add(para);
                            list.ListItems.Add(item);
                        }
                        doc.Blocks.Add(list);
                        break;

                    default:   // "p" and anything unexpected
                        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                        AddInlines(p.Inlines, element, default);
                        doc.Blocks.Add(p);
                        break;
                }
            }
            if (doc.Blocks.Count == 0) doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 4) });
            return true;
        }

        private static void AddInlines(InlineCollection target, XElement parent, Fmt fmt)
        {
            foreach (var node in parent.Nodes())
            {
                if (node is XText text)
                {
                    target.Add(MakeRun(text.Value, fmt));
                }
                else if (node is XElement e)
                {
                    switch (e.Name.LocalName)
                    {
                        case "b": AddInlines(target, e, fmt with { Bold = true }); break;
                        case "i": AddInlines(target, e, fmt with { Italic = true }); break;
                        case "u": AddInlines(target, e, fmt with { Underline = true }); break;
                        case "s": AddInlines(target, e, fmt with { Strike = true }); break;
                        case "br": target.Add(new LineBreak()); break;
                        case "img":
                            double.TryParse((string?)e.Attribute("w"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double w);
                            double.TryParse((string?)e.Attribute("h"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h);
                            string file = (string?)e.Attribute("f") ?? "";
                            if (file.Length > 0 && w > 0 && h > 0)
                                target.Add(MakeImage(new NoteImageInfo(file, w, h, (string?)e.Attribute("s") ?? "")));
                            break;
                        default: AddInlines(target, e, fmt); break;
                    }
                }
            }
        }

        private static Run MakeRun(string text, Fmt fmt)
        {
            var run = new Run(text);
            if (fmt.Bold) run.FontWeight = FontWeights.Bold;
            if (fmt.Italic) run.FontStyle = FontStyles.Italic;
            if (fmt.Underline || fmt.Strike)
            {
                var decorations = new TextDecorationCollection();
                if (fmt.Underline) foreach (var d in TextDecorations.Underline) decorations.Add(d);
                if (fmt.Strike) foreach (var d in TextDecorations.Strikethrough) decorations.Add(d);
                run.TextDecorations = decorations;
            }
            return run;
        }

        // =================================================================
        //  Document -> text
        // =================================================================

        public static string Serialize(FlowDocument doc)
        {
            var sb = new StringBuilder();
            foreach (var block in doc.Blocks) WriteBlock(sb, block);
            return sb.ToString();
        }

        private static void WriteBlock(StringBuilder sb, Block block)
        {
            switch (block)
            {
                case Paragraph p:
                    sb.Append("<p>");
                    WriteInlines(sb, p.Inlines);
                    sb.Append("</p>");
                    break;

                case List list:
                    sb.Append("<ul>");
                    foreach (var item in list.ListItems)
                    {
                        sb.Append("<li>");
                        bool first = true;
                        foreach (var inner in item.Blocks.OfType<Paragraph>())
                        {
                            if (!first) sb.Append("<br/>");
                            WriteInlines(sb, inner.Inlines);
                            first = false;
                        }
                        sb.Append("</li>");
                    }
                    sb.Append("</ul>");
                    break;

                case Section section:
                    foreach (var inner in section.Blocks) WriteBlock(sb, inner);
                    break;

                default:
                    sb.Append("<p>").Append(Escape(new TextRange(block.ContentStart, block.ContentEnd).Text)).Append("</p>");
                    break;
            }
        }

        private static void WriteInlines(StringBuilder sb, InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        WriteRun(sb, run);
                        break;
                    case LineBreak:
                        sb.Append("<br/>");
                        break;
                    case InlineUIContainer { Child: StackPanel { Tag: NoteImageInfo info } }:
                        sb.Append("<img f=\"").Append(Escape(info.File)).Append("\" w=\"").Append(info.Width.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                          .Append("\" h=\"").Append(info.Height.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                          .Append("\" s=\"").Append(Escape(info.Source)).Append("\"/>");
                        break;
                    case Span span:
                        WriteInlines(sb, span.Inlines);
                        break;
                }
            }
        }

        private static void WriteRun(StringBuilder sb, Run run)
        {
            if (run.Text.Length == 0) return;
            bool bold = run.FontWeight.ToOpenTypeWeight() >= 600;
            bool italic = run.FontStyle == FontStyles.Italic || run.FontStyle == FontStyles.Oblique;
            var (underline, strike) = DecorationsOf(run);

            if (bold) sb.Append("<b>");
            if (italic) sb.Append("<i>");
            if (underline) sb.Append("<u>");
            if (strike) sb.Append("<s>");
            sb.Append(Escape(run.Text));
            if (strike) sb.Append("</s>");
            if (underline) sb.Append("</u>");
            if (italic) sb.Append("</i>");
            if (bold) sb.Append("</b>");
        }

        /// <summary>Underline and strikethrough are set on the run or on a span around it, so look up the chain.</summary>
        private static (bool Underline, bool Strike) DecorationsOf(TextElement start)
        {
            bool underline = false, strike = false;
            DependencyObject? current = start;
            while (current is TextElement element)
            {
                TextDecorationCollection? decorations = element switch
                {
                    Inline inline => inline.TextDecorations,
                    Paragraph paragraph => paragraph.TextDecorations,
                    _ => null,
                };
                if (decorations != null)
                {
                    foreach (var d in decorations)
                    {
                        if (d.Location == TextDecorationLocation.Underline) underline = true;
                        else if (d.Location == TextDecorationLocation.Strikethrough) strike = true;
                    }
                }
                current = element.Parent;
            }
            return (underline, strike);
        }

        private static string Escape(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (!XmlConvert.IsXmlChar(c) && !char.IsSurrogate(c)) continue;
                sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => c.ToString() });
            }
            return sb.ToString();
        }

        /// <summary>The note as plain text: one line per paragraph, "• " before list items, pictures left out.</summary>
        public static string PlainText(FlowDocument doc)
        {
            var lines = new List<string>();
            foreach (var block in doc.Blocks)
            {
                switch (block)
                {
                    case Paragraph p:
                        lines.Add(TextOf(p));
                        break;
                    case List list:
                        foreach (var item in list.ListItems)
                            lines.Add("• " + string.Join(" ", item.Blocks.OfType<Paragraph>().Select(TextOf)));
                        break;
                    default:
                        lines.Add(new TextRange(block.ContentStart, block.ContentEnd).Text.Trim('\r', '\n').Replace("￼", ""));
                        break;
                }
            }
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return string.Join("\r\n", lines);
        }

        private static string TextOf(Paragraph p) =>
            new TextRange(p.ContentStart, p.ContentEnd).Text.Replace("￼", "").TrimEnd('\r', '\n');

        // =================================================================
        //  Pictures
        // =================================================================

        /// <summary>A picture in the editor: the image, and the source under it when there is one. Right-click removes it.</summary>
        public static InlineUIContainer MakeImage(NoteImageInfo info) => new(MakePanel(info));

        private static StackPanel MakePanel(NoteImageInfo info)
        {
            var panel = new StackPanel { Tag = info, Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
            var bitmap = LoadBitmap(info.File);
            if (bitmap != null)
            {
                panel.Children.Add(new Image { Source = bitmap, Width = info.Width, Height = info.Height, Stretch = Stretch.Uniform });
            }
            else
            {
                var missing = new TextBlock { Text = Loc.T("Str_Notes_ImageMissing", "[picture not found]"), FontStyle = FontStyles.Italic, Opacity = 0.6 };
                panel.Children.Add(missing);
            }

            if (!string.IsNullOrWhiteSpace(info.Source))
            {
                var caption = new TextBlock
                {
                    Text = Loc.F("Str_Notes_ImageSource", "Source: {0}", info.Source),
                    FontSize = 11,
                    FontStyle = FontStyles.Italic,
                    Opacity = 0.65,
                    MaxWidth = Math.Max(info.Width, 160),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = info.Source,
                };
                panel.Children.Add(caption);
            }

            var remove = new MenuItem { Header = Loc.T("Str_Notes_RemovePicture", "Remove picture") };
            remove.Click += (_, _) =>
            {
                if (panel.Parent is InlineUIContainer container) container.SiblingInlines.Remove(container);
            };
            panel.ContextMenu = new System.Windows.Controls.ContextMenu { Items = { remove } };
            return panel;
        }

        private static BitmapSource? LoadBitmap(string file)
        {
            if (file.Length == 0 || !string.Equals(Path.GetFileName(file), file, StringComparison.Ordinal)) return null;   // a name, never a path
            try
            {
                string path = Path.Combine(NotesManager.ImagesFolder, file);
                if (!File.Exists(path)) return null;
                var bitmap = new BitmapImage();
                using var stream = File.OpenRead(path);
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = (int)(MaxImageWidth * 2);   // sharp on high-DPI screens, still small in memory
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>Strikethrough has no built-in command: toggles it on the selection, leaving underline alone.</summary>
        public static void ToggleStrike(RichTextBox box)
        {
            var selection = box.Selection;
            var current = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
            bool has = current != null && current.Any(d => d.Location == TextDecorationLocation.Strikethrough);
            var next = new TextDecorationCollection();
            if (current != null)
                foreach (var d in current)
                    if (d.Location != TextDecorationLocation.Strikethrough) next.Add(d);
            if (!has) foreach (var d in TextDecorations.Strikethrough) next.Add(d);
            selection.ApplyPropertyValue(Inline.TextDecorationsProperty, next);
        }

        /// <summary>A copied picture (a screenshot, an image from a web page) is pasted as a picture, with its source; anything with text pastes as usual.</summary>
        public static void AttachPaste(RichTextBox box)
        {
            DataObject.AddPastingHandler(box, (_, e) =>
            {
                try
                {
                    if (!e.DataObject.GetDataPresent(DataFormats.Bitmap) || e.DataObject.GetDataPresent(DataFormats.UnicodeText) || e.DataObject.GetDataPresent(DataFormats.Text)) return;
                    if (System.Windows.Clipboard.GetImage() is not { } image) return;
                    image.Freeze();
                    e.CancelCommand();
                    InsertImage(box, image, NoteSourceDetector.FromClipboard());
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException or NotSupportedException)
                {
                    // the clipboard was busy: let the normal paste try
                }
            });
        }

        /// <summary>Saves the picture as a PNG in the notes pictures folder and inserts it at the caret.</summary>
        /// <summary>Set on a box's Tag to refuse pictures: they are saved as separate files, which a password lock cannot cover.</summary>
        public const string NoPicturesTag = "no-pictures";

        public static bool InsertImage(RichTextBox box, BitmapSource source, string sourceText)
        {
            if (box.Tag as string == NoPicturesTag) return false;
            try
            {
                string file = Guid.NewGuid().ToString("N") + ".png";
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                Directory.CreateDirectory(NotesManager.ImagesFolder);
                AtomicFile.WriteAllBytes(Path.Combine(NotesManager.ImagesFolder, file), ms.ToArray());

                double width = source.Width, height = source.Height;
                if (width > MaxImageWidth)
                {
                    height *= MaxImageWidth / width;
                    width = MaxImageWidth;
                }
                var position = box.CaretPosition.GetInsertionPosition(LogicalDirection.Forward);
                var inserted = new InlineUIContainer(MakePanel(new NoteImageInfo(file, Math.Round(width), Math.Round(height), sourceText)), position);
                box.CaretPosition = inserted.ElementEnd;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
            {
                return false;
            }
        }
    }
}
