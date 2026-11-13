using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Application = System.Windows.Application;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Path = System.IO.Path;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// PDF Reader &amp; Signer - UI only. Pages are drawn with Windows' own PDF renderer; marks (text, date, signature, check, cross, line, highlight)
    /// are placed on top with the mouse and written onto a new copy by <see cref="PdfReaderManager"/>. The original is never changed.
    /// This is a visual signature, not a certificate-based digital signature.
    /// <para>
    /// This file: the list of PDFs, opening one, zoom and drawing the pages. A folder fills the list with its PDFs but only the one that is
    /// open is held by the renderer, and only the pages near the screen keep their picture (the others are let go as you scroll), so memory
    /// stays flat however many PDFs or pages there are. Marks: PdfReaderView.Marks.cs. Signature and its dialog: PdfReaderView.Signature.cs.
    /// </para>
    /// </summary>
    public partial class PdfReaderView : UserControl
    {
        private const double OptionsExpandedWidth = 280;
        private const double OptionsCollapsedWidth = 56;
        private const double MinZoom = 0.3, MaxZoom = 4.0;
        private const double PageGap = 14, ListPadding = 4;   // the page template's bottom margin and the list's top margin
        private static string DialogTitle => Loc.T("Str_Tool_pdf-reader_Name", "PDF Reader & Signer");

        private readonly System.Collections.ObjectModel.ObservableCollection<PdfReaderPage> _pages = new();
        private readonly BulkObservableCollection<PdfReaderFile> _files = new();
        private readonly Dictionary<PdfReaderPage, Canvas> _canvases = new();
        private readonly Dictionary<PdfMark, Border> _elements = new();
        private readonly HashSet<PdfReaderPage> _rendering = new();
        private readonly SemaphoreSlim _renderGate = new(2);
        private readonly DispatcherTimer _windowTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
        private PdfExtrasManager.PageRenderer? _renderer;
        private CancellationTokenSource _renderCts = new();
        private int _generation;   // bumped when pictures become out of date (zoom, another PDF), so a late picture is dropped
        private double[] _tops = Array.Empty<double>();
        private PdfReaderSettings _settings = new();
        private string _path = "";
        private string _password = "";
        private double _zoom = 1.0;
        private bool _loadedOnce, _optionsCollapsed, _busy, _scanning;

        public PdfReaderView()
        {
            InitializeComponent();
            PageList.ItemsSource = _pages;
            InputCard.ItemsSource = _files;
            InputCard.FileFilterProvider = () => "PDF (*.pdf)|*.pdf|" + T("Str_PdfReader_FilterAll", "All files") + "|*.*";
            Scroller.PreviewMouseWheel += Scroller_PreviewMouseWheel;
            Scroller.ScrollChanged += (_, _) => { _windowTimer.Stop(); _windowTimer.Start(); };
            _windowTimer.Tick += (_, _) => { _windowTimer.Stop(); UpdateWindow(); };
            PreviewKeyDown += View_PreviewKeyDown;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops drawing pages and lets go of the file.</summary>
        public void CancelActiveRun()
        {
            _windowTimer.Stop();
            if (_modal != null) CloseModal(null);
            _renderCts.Cancel();
            _renderer?.Dispose();
            _renderer = null;
        }

        private static string T(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _settings = PdfReaderManager.LoadSettings();
            var saved = PdfReaderManager.LoadSignature();
            if (saved != null) SetSignature(saved, remember: false);
            UpdateZoomText();
            UpdateAll();
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? OptionsCollapsedWidth : OptionsExpandedWidth);
        }

        // =================================================================
        //  The list of PDFs
        // =================================================================

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e) => _ = AddPathsAsync(e.Paths);

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files) _ = AddPathsAsync(files);
        }

        /// <summary>
        /// Files and folders were chosen or dropped: every PDF in them joins the list. One file chosen on its own is opened; a folder
        /// only opens its first PDF when nothing is open yet, so it never pulls the page away from what you are doing.
        /// </summary>
        private async Task AddPathsAsync(IReadOnlyList<string> paths)
        {
            if (_scanning || _busy) return;
            _scanning = true;
            TxtProgress.Text = T("Str_PdfReader_Looking", "Looking for PDFs…");
            TxtProgressDetail.Text = "";
            List<string> found;
            try
            {
                found = await Task.Run(() => PdfReaderManager.FindPdfs(paths));
            }
            finally
            {
                _scanning = false;
            }

            if (found.Count == 0)
            {
                TxtProgress.Text = T("Str_PdfReader_NoPdf", "That is not a PDF.");
                TxtProgressDetail.Text = "";
                return;
            }

            var known = new HashSet<string>(_files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            var added = new List<PdfReaderFile>();
            foreach (string pdf in found)
                if (known.Add(pdf)) added.Add(new PdfReaderFile { Path = pdf, Name = Path.GetFileName(pdf) });
            _files.AddRange(added);
            InputCard.SetEmpty(_files.Count == 0);

            bool singleFile = paths.Count == 1 && File.Exists(paths[0]);
            var target = singleFile ? _files.FirstOrDefault(f => string.Equals(f.Path, found[0], StringComparison.OrdinalIgnoreCase))
                : _path.Length == 0 ? added.FirstOrDefault() : null;
            UpdateFileBadge();
            if (target != null) await OpenAsync(target.Path, null);
            else
            {
                TxtProgress.Text = Loc.N(added.Count, "Str_PdfReader_AddedOne", "{0} PDF added to the list.", "Str_PdfReader_AddedMany", "{0} PDFs added to the list.", added.Count);
                TxtProgressDetail.Text = T("Str_PdfReader_PickHint", "Click a PDF in the list to open it.");
            }
        }

        private void FileRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: PdfReaderFile file } && !_busy) _ = OpenAsync(file.Path, null);
        }

        private void BtnRemoveFile_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (_busy || (sender as FrameworkElement)?.Tag is not PdfReaderFile file) return;
            bool isOpen = string.Equals(file.Path, _path, StringComparison.OrdinalIgnoreCase);
            if (isOpen)
            {
                if (!ConfirmDiscard()) return;
                ReleaseDocument();
            }
            _files.Remove(file);
            InputCard.SetEmpty(_files.Count == 0);
            UpdateFileBadge();
            UpdateAll();
            if (isOpen) ShowIdle();
        }

        /// <summary>Clear on the Input card empties the list and closes the open PDF.</summary>
        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_busy || !ConfirmDiscard()) return;
            ReleaseDocument();
            _files.Clear();
            InputCard.SetEmpty(true);
            UpdateFileBadge();
            UpdateAll();
            ShowIdle();
        }

        private void BtnPrevPdf_Click(object sender, RoutedEventArgs e) => Step(-1);

        private void BtnNextPdf_Click(object sender, RoutedEventArgs e) => Step(1);

        private void Step(int direction)
        {
            if (_busy) return;
            int at = _files.ToList().FindIndex(f => f.IsCurrent) + direction;
            if (at >= 0 && at < _files.Count) _ = OpenAsync(_files[at].Path, null);
        }

        private void SetCurrent(string path)
        {
            foreach (var file in _files) file.IsCurrent = string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase);
            UpdateFileBadge();
        }

        private void UpdateFileBadge()
        {
            int index = _files.ToList().FindIndex(f => f.IsCurrent);
            BadgeFiles.Visibility = _files.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            TxtFileInfo.Text = $"{index + 1} / {_files.Count}";
        }

        /// <summary>True when there is nothing unsaved, or the user agrees to lose it.</summary>
        private bool ConfirmDiscard()
        {
            if (!_unsaved) return true;
            return MessageBox.Show(T("Str_PdfReader_DiscardMarks", "You have marks on the open PDF that are not saved. Open another PDF anyway?"), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        // =================================================================
        //  Opening
        // =================================================================

        private void BtnUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (_path.Length > 0) _ = OpenAsync(_path, PwdBox.Password);
        }

        private void PwdBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnUnlock_Click(sender, e);
        }

        /// <summary>
        /// Lets go of the open PDF: stops the drawing, drops every page picture and mark, and forgets the file. Called before another PDF
        /// takes its place, so only one PDF is ever held.
        /// </summary>
        private void ReleaseDocument()
        {
            _windowTimer.Stop();
            _generation++;
            _renderCts.Cancel();
            _renderer?.Dispose();
            _renderer = null;
            _renderCts = new CancellationTokenSource();
            bool hadPages = _pages.Count > 0;
            foreach (var page in _pages) { page.Source = null; page.Marks.Clear(); }
            _pages.Clear();
            _canvases.Clear();
            _elements.Clear();
            _rendering.Clear();
            _tops = Array.Empty<double>();
            _selected = null;
            _unsaved = false;
            _path = "";
            _password = "";
            PwdBox.Clear();
            PanelPasswordCard.Visibility = Visibility.Collapsed;
            TxtPageInfo.Text = "";
            BadgePages.Visibility = Visibility.Collapsed;
            foreach (var file in _files) file.IsCurrent = false;
            UpdateSelectedPanel();
            // The page pictures are native memory behind small managed objects; collect them now rather than whenever the GC gets round to it.
            if (hadPages) Dispatcher.InvokeAsync(() => GC.Collect(), DispatcherPriority.ApplicationIdle);
        }

        private async Task OpenAsync(string path, string? password)
        {
            if (_busy) return;
            bool sameFile = string.Equals(_path, path, StringComparison.OrdinalIgnoreCase);
            if (!sameFile && !ConfirmDiscard()) return;

            _busy = true;
            bool askPassword = false;
            TxtProgress.Text = T("Str_PdfReader_Opening", "Opening…");
            TxtProgressDetail.Text = "";
            try
            {
                var renderer = await PdfExtrasManager.PageRenderer.OpenAsync(path, password);
                List<PdfReaderPage> pages;
                try
                {
                    pages = await Task.Run(() =>
                    {
                        var list = new List<PdfReaderPage>(renderer.PageCount);
                        for (int i = 0; i < renderer.PageCount; i++)
                        {
                            var (w, h) = renderer.SizeOf(i);
                            list.Add(new PdfReaderPage { Index = i, WidthPt = w, HeightPt = h });
                        }
                        return list;
                    });
                }
                catch
                {
                    renderer.Dispose();
                    throw;
                }

                ReleaseDocument();
                _renderer = renderer;
                _path = path;
                _password = password ?? "";
                foreach (var p in pages) _pages.Add(p);

                SetCurrent(path);
                TxtPageInfo.Text = Loc.N(_pages.Count, "Str_PdfReader_PagesOne", "{0} page", "Str_PdfReader_Pages", "{0} pages", _pages.Count);
                BadgePages.Visibility = Visibility.Visible;
                _zoom = FitWidthZoom();
                ApplyZoom(redraw: false);
                Scroller.ScrollToTop();
                UpdateAll();
                ShowIdle();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                ReleaseDocument();
                _path = path;   // so Unlock can try again with a password
                SetCurrent(path);
                bool needsPassword = PdfExtrasManager.PageRenderer.IsPasswordProblem(ex) || (ex.InnerException != null && PdfExtrasManager.PageRenderer.IsPasswordProblem(ex.InnerException));
                PanelPasswordCard.Visibility = needsPassword ? Visibility.Visible : Visibility.Collapsed;
                UpdateAll();
                TxtProgress.Text = T("Str_PdfReader_CantOpen", "This PDF could not be opened.");
                TxtProgressDetail.Text = needsPassword
                    ? T("Str_PdfReader_CantOpenDetail", "If it has a password, type it in the sidebar and press Unlock.")
                    : T("Str_PdfReader_CantOpenOther", "Windows could not read this file: ") + ex.Message;
                askPassword = needsPassword;
            }
            finally
            {
                _busy = false;
                UpdateSaveEnabled();
            }

            // Ask for the password right away instead of waiting for the sidebar card; a wrong one asks again, Cancel stops.
            if (askPassword)
            {
                string? typed = PdfPasswordDialog.Ask(DialogTitle, System.IO.Path.GetFileName(path), wrong: !string.IsNullOrEmpty(password));
                if (typed != null) await OpenAsync(path, typed);
            }
        }

        private void ShowIdle()
        {
            TxtProgress.Text = _pages.Count == 0
                ? T("Str_PdfReader_Idle", "Open a PDF to read it, sign it or fill it in.")
                : T("Str_PdfReader_IdleOpen", "Add your signature, text, a date or a check mark, then save a copy.");
            TxtProgressDetail.Text = T("Str_PdfReader_IdleDetail", "Your original is never changed. The signed PDF is saved as a new file.");
        }

        // =================================================================
        //  Pages: zoom, and drawing only what is near the screen
        // =================================================================

        private double FitWidthZoom()
        {
            if (_pages.Count == 0) return 1.0;
            double widest = _pages.Max(p => p.WidthPt);
            double room = Math.Max(200, Scroller.ActualWidth - 44);
            return Math.Clamp(room / widest, MinZoom, MaxZoom);
        }

        private void ApplyZoom(bool redraw)
        {
            _generation++;
            foreach (var page in _pages)
            {
                page.DisplayWidth = page.WidthPt * _zoom;
                page.DisplayHeight = page.HeightPt * _zoom;
                if (redraw) page.Source = null;
            }
            RebuildOffsets();
            UpdateZoomText();
            if (redraw)
            {
                foreach (var (page, canvas) in _canvases.ToList()) FillCanvas(page, canvas);
                _rendering.Clear();
                UpdateWindow();
            }
        }

        private void UpdateZoomText() => TxtZoom.Text = $"{Math.Round(_zoom * 100)}%";

        private void SetZoom(double zoom)
        {
            zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
            if (Math.Abs(zoom - _zoom) < 0.001 || _pages.Count == 0) return;
            _zoom = zoom;
            ApplyZoom(redraw: true);
        }

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.2);

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.2);

        private void BtnZoomFit_Click(object sender, RoutedEventArgs e) => SetZoom(FitWidthZoom());

        private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            SetZoom(e.Delta > 0 ? _zoom * 1.1 : _zoom / 1.1);
            e.Handled = true;
        }

        /// <summary>Where each page starts, from the top of the list, at the current zoom.</summary>
        private void RebuildOffsets()
        {
            _tops = new double[_pages.Count];
            double y = ListPadding;
            for (int i = 0; i < _pages.Count; i++)
            {
                _tops[i] = y;
                y += _pages[i].DisplayHeight + PageGap;
            }
        }

        /// <summary>The stretch of the list that keeps its page pictures: the screen plus about a screen above and below.</summary>
        private (double Top, double Bottom) KeepRange()
        {
            double view = Math.Max(Scroller.ViewportHeight, 700);
            double offset = Scroller.VerticalOffset;
            return (offset - view * 0.75, offset + view * 1.75);
        }

        private bool IsNear(PdfReaderPage page, (double Top, double Bottom) range) =>
            page.Index < _tops.Length && _tops[page.Index] + page.DisplayHeight >= range.Top && _tops[page.Index] <= range.Bottom;

        /// <summary>Draws the pages that came near the screen and lets go of the pictures of the ones that went far away.</summary>
        private void UpdateWindow()
        {
            if (_renderer == null) return;
            var range = KeepRange();
            foreach (var page in _pages)
            {
                if (IsNear(page, range))
                {
                    if (page.Source == null && _canvases.ContainsKey(page)) _ = RenderPageAsync(page);
                }
                else if (page.Source != null)
                {
                    page.Source = null;
                }
            }
        }

        private void PageBorder_Loaded(object sender, RoutedEventArgs e) => AttachPage(sender as Border);

        private void PageBorder_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => AttachPage(sender as Border);

        /// <summary>A page container appeared (or got another page): fill its canvas with the marks and draw the page if it is near the screen.</summary>
        private void AttachPage(Border? border)
        {
            if (border?.DataContext is not PdfReaderPage page || border.Child is not Grid { Children.Count: > 1 } grid || grid.Children[1] is not Canvas canvas) return;
            foreach (var key in _canvases.Where(kv => ReferenceEquals(kv.Value, canvas) && kv.Key != page).Select(kv => kv.Key).ToList()) _canvases.Remove(key);
            _canvases[page] = canvas;
            FillCanvas(page, canvas);
            if (page.Source == null && IsNear(page, KeepRange())) _ = RenderPageAsync(page);
        }

        private async Task RenderPageAsync(PdfReaderPage page)
        {
            var renderer = _renderer;
            if (renderer == null || !_rendering.Add(page)) return;
            var token = _renderCts.Token;
            int generation = _generation;
            int longSide = (int)Math.Clamp(Math.Max(page.DisplayWidth, page.DisplayHeight) * 1.25, 300, 3200);
            try
            {
                await _renderGate.WaitAsync(token);
                try
                {
                    // Drawn meanwhile, scrolled far away while it waited, or out of date.
                    if (page.Source != null || generation != _generation || !IsNear(page, KeepRange())) return;
                    var picture = await renderer.RenderAsync(page.Index, longSide, token);
                    if (!token.IsCancellationRequested && generation == _generation && ReferenceEquals(renderer, _renderer) && IsNear(page, KeepRange()))
                        page.Source = picture;
                }
                finally
                {
                    _renderGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // A new PDF was opened or the app is closing.
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ObjectDisposedException)
            {
                // One page could not be drawn; it stays blank.
            }
            finally
            {
                _rendering.Remove(page);
            }
        }

        // =================================================================
        //  Saving
        // =================================================================

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var marks = _pages.SelectMany(p => p.Marks).ToList();
            if (_path.Length == 0 || marks.Count == 0 || _busy) return;

            string folder = Path.GetDirectoryName(_path) ?? "";
            string suggestion = SafeFileOps.UniquePath(Path.Combine(folder, Path.GetFileNameWithoutExtension(_path) + "-signed.pdf"));
            var dialog = new SaveFileDialog
            {
                Filter = "PDF (*.pdf)|*.pdf",
                Title = DialogTitle,
                InitialDirectory = folder,
                FileName = Path.GetFileName(suggestion),
                OverwritePrompt = true,
            };
            if (dialog.ShowDialog() != true) return;
            string output = dialog.FileName;
            if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(T("Str_PdfReader_NotOriginal", "Choose a different name: the original PDF is never replaced."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _busy = true;
            BtnSave.IsEnabled = false;
            TxtProgress.Text = T("Str_PdfReader_Saving", "Saving…");
            TxtProgressDetail.Text = "";
            var widths = _pages.ToDictionary(p => p.Index, p => p.WidthPt);
            string input = _path, password = _password;
            try
            {
                await Task.Run(() => PdfReaderManager.Save(input, password, marks, output, widths));
                _unsaved = false;
                TxtProgress.Text = T("Str_PdfReader_Saved", "Saved a signed copy.");
                TxtProgressDetail.Text = output;
                ShowInFolder(output);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                TxtProgress.Text = T("Str_PdfReader_SaveFailed", "The copy could not be saved.");
                TxtProgressDetail.Text = ex.Message;
            }
            finally
            {
                _busy = false;
                UpdateSaveEnabled();
            }
        }

        private void UpdateSaveEnabled() => BtnSave.IsEnabled = !_busy && _pages.Any(p => p.Marks.Count > 0);

        private static void ShowInFolder(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Explorer couldn't start; the path is shown in the status line.
            }
        }
    }
}
