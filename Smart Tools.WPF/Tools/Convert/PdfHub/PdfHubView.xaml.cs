using System.Windows;
using System.Windows.Controls;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// PDF Tools: one tool, two pages. Merge &amp; split is the PDF Toolkit and Pages &amp; protect is what used to be
    /// PDF Extras, both unchanged. This view only switches between them and lets each one release its files on close.
    /// </summary>
    public partial class PdfHubView : UserControl
    {
        private enum Page { MergeSplit, PagesProtect }

        private static Page _lastPage = Page.MergeSplit;   // remembered while the app runs

        private readonly Dictionary<Page, UserControl> _pages = new();
        private bool _switching;

        public PdfHubView()
        {
            InitializeComponent();
            Show(_lastPage);
        }

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (_switching || sender is not RadioButton { Tag: string tag } || !Enum.TryParse(tag, out Page page)) return;
            Show(page);
        }

        private UserControl GetPage(Page page)
        {
            if (!_pages.TryGetValue(page, out var view))
            {
                view = page == Page.PagesProtect ? new PdfExtrasView() : new PdfToolkitView();
                _pages[page] = view;
            }
            return view;
        }

        private void Show(Page page)
        {
            _switching = true;
            (page == Page.PagesProtect ? RadioPages : RadioMerge).IsChecked = true;
            _switching = false;

            _lastPage = page;
            // Swapping the content unloads the old page and loads the new one, like opening another tool.
            PageHost.Content = GetPage(page);
            TxtHint.Text = page == Page.PagesProtect
                ? "Change the pages of a PDF, its details or its password."
                : "Join several PDFs into one, cut one into parts, or make it smaller.";
        }

        /// <summary>Called from MainWindow.Window_Closing: lets every page that was opened stop its run and save its setup.</summary>
        public void CancelActiveRun()
        {
            foreach (var view in _pages.Values)
            {
                switch (view)
                {
                    case PdfToolkitView t: t.CancelActiveRun(); break;
                    case PdfExtrasView x: x.CancelActiveRun(); break;
                }
            }
        }
    }
}
