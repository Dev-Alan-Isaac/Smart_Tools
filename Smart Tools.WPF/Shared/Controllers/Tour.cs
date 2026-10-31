using System.Windows;
using System.Windows.Media;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Guided-tour support. A view opts in by tagging the controls worth explaining:
    /// <c>ctl:Tour.Key="Str_Tour_X_Options" ctl:Tour.Order="1"</c>. The step's title and
    /// text are the language keys <c>&lt;Key&gt;_Title</c> and <c>&lt;Key&gt;_Text</c>.
    /// The shell's Help button collects the tagged controls of the page that is open
    /// right now - the tour never leaves that page.
    /// </summary>
    public static class Tour
    {
        public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
            "Key", typeof(string), typeof(Tour), new PropertyMetadata(null));

        public static readonly DependencyProperty OrderProperty = DependencyProperty.RegisterAttached(
            "Order", typeof(int), typeof(Tour), new PropertyMetadata(0));

        public static string? GetKey(DependencyObject element) => (string?)element.GetValue(KeyProperty);
        public static void SetKey(DependencyObject element, string? value) => element.SetValue(KeyProperty, value);
        public static int GetOrder(DependencyObject element) => (int)element.GetValue(OrderProperty);
        public static void SetOrder(DependencyObject element, int value) => element.SetValue(OrderProperty, value);

        /// <summary>One stop of a tour: the control to highlight and what to say about it.</summary>
        public sealed record Step(FrameworkElement Target, string Title, string Text);

        /// <summary>True when the page has at least one tagged control (visible or not).</summary>
        public static bool HasSteps(DependencyObject? root) => Find(root, requireVisible: false).Count > 0;

        /// <summary>
        /// The steps of a page, in order. Controls that are hidden, collapsed to nothing, or
        /// have no text in the active language are skipped. Looked up at call time, so a
        /// language switch or a collapsed sidebar is always respected.
        /// </summary>
        public static List<Step> Collect(DependencyObject? root)
        {
            var steps = new List<Step>();
            foreach (var element in Find(root, requireVisible: true))
            {
                string key = GetKey(element)!;
                string title = Lookup(key + "_Title");
                string text = Lookup(key + "_Text");
                if (title.Length == 0 && text.Length == 0) continue;
                steps.Add(new Step(element, title, text));
            }
            return steps;
        }

        private static string Lookup(string key) =>
            System.Windows.Application.Current?.TryFindResource(key) as string ?? "";

        private static List<FrameworkElement> Find(DependencyObject? root, bool requireVisible)
        {
            var found = new List<(FrameworkElement Element, int Order, int Index)>();
            int index = 0;
            Walk(root);

            void Walk(DependencyObject? node)
            {
                if (node is null) return;
                if (node is FrameworkElement fe && !string.IsNullOrEmpty(GetKey(fe))
                    && (!requireVisible || IsShown(fe)))
                    found.Add((fe, GetOrder(fe), index++));

                int count = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
                for (int i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(node, i));
            }

            return found.OrderBy(f => f.Order).ThenBy(f => f.Index).Select(f => f.Element).ToList();
        }

        private static bool IsShown(FrameworkElement element) =>
            element.IsVisible && element.ActualWidth > 8 && element.ActualHeight > 8;
    }
}
