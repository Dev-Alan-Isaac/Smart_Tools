using MaterialDesignThemes.Wpf;
using Microsoft.Win32;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using Smart_Tools.WPF.Modules;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Border = System.Windows.Controls.Border;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// File Search - UI only. Finding files, matching (text, wildcard or
    /// several regex patterns at once), "each value once", export and
    /// settings are in <see cref="FileSearchManager"/>. Rows stream in as
    /// they're found, in folder/file/line order. Folders picked here are
    /// kept for this session only.
    /// </summary>
    public partial class FileSearchView : UserControl
    {
        private const double NarrowColumnThreshold = 1000;
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_search-files_Name", "File Search");

        private readonly ObservableCollection<FileSearchFolder> _folders = new();
        private readonly BulkObservableCollection<FileSearchResult> _results = new();
        private readonly ObservableCollection<FileSearchPatternChoice> _patterns = new();
        private readonly Dictionary<SavedSearchPattern, FileSearchPatternChoice> _savedChoices = new();
        private readonly ICollectionView _resultsView;
        private readonly DispatcherTimer _saveTimer;
        private readonly DispatcherTimer _filterTimer;
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly bool _viewConstructed;

        private FileSearchMode _mode = FileSearchMode.Text;
        private FileSearchExportFormat _exportFormat = FileSearchExportFormat.Csv;
        private CancellationTokenSource? _searchCts;
        private FileSearchSummary? _lastSummary;
        private bool _lastWasUnique;
        private bool _isSearching;
        private bool _columnsAreCompact;
        private bool _optionsCollapsed;
        private bool _loadedOnce;
        private bool _subscribed;
        private bool _applyingSettings;
        private string _filter = "";

        public FileSearchView()
        {
            InitializeComponent();

            InputCard.ItemsSource = _folders;
            LstRegexPatterns.ItemsSource = _patterns;
            _resultsView = CollectionViewSource.GetDefaultView(_results);
            _resultsView.Filter = o => _filter.Length == 0 || o is FileSearchResult r &&
                (r.MatchText.Contains(_filter, StringComparison.OrdinalIgnoreCase) || r.FullPath.Contains(_filter, StringComparison.OrdinalIgnoreCase));
            DataGridResults.ItemsSource = _resultsView;

            _folders.CollectionChanged += (_, _) => UpdateFolderState();
            _patterns.CollectionChanged += (_, _) => UpdatePatternCount();

            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                SaveSettings();
            };
            _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _filterTimer.Tick += (_, _) =>
            {
                _filterTimer.Stop();
                _filter = TxtResultFilter.Text.Trim();
                _resultsView.Refresh();
                UpdateResultState();
            };

            _viewConstructed = true;
            UpdateFolderState();
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a search and saves the options.</summary>
        public void CancelActiveRun()
        {
            try
            {
                _searchCts?.Cancel();
                _lifetimeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                SaveSettings();
            }
        }

        private bool Ignore => !_viewConstructed || _applyingSettings;

        // =================================================================
        //  Layout and lifetime
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            // Subscribe on every Loaded: MainWindow reuses this view, and the old
            // code unsubscribed on Unloaded but never subscribed again, so after
            // the first visit the saved patterns stopped following Settings.
            SubscribeSavedPatterns();
            ApplyResponsiveColumns(ActualWidth);
            if (_loadedOnce) return;
            _loadedOnce = true;
            ApplySettings(FileSearchManager.LoadSettings());
        }

        private void Root_Unloaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeSavedPatterns();
            if (_saveTimer.IsEnabled)
            {
                _saveTimer.Stop();
                SaveSettings();
            }
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyResponsiveColumns(e.NewSize.Width);

        private void ApplyResponsiveColumns(double width)
        {
            bool compact = width < NarrowColumnThreshold;
            if (compact == _columnsAreCompact) return;
            _columnsAreCompact = compact;
            var visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ColLine.Visibility = visibility;
            ColType.Visibility = visibility;
        }

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            OptionsBottomPanel.Visibility = visibility;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            ApplyResponsiveColumns(ActualWidth);
        }

        // =================================================================
        //  Settings (how the search is set up - never the folders)
        // =================================================================

        private void ApplySettings(FileSearchSettings s)
        {
            _applyingSettings = true;
            try
            {
                TxtSearchText.Text = s.SearchText;
                ChkSearchFileNames.IsChecked = s.SearchFileNames;
                ChkSearchFileContents.IsChecked = s.SearchFileContents;
                ChkCaseSensitive.IsChecked = !s.IgnoreCase;
                ChkWholeWordOnly.IsChecked = s.WholeWord;
                InputCard.IncludeSubfolders = s.IncludeSubfolders;
                ChkUniqueMatchesOnly.IsChecked = s.UniqueValues;
                ChkRegexFirstMatchOnly.IsChecked = s.FirstMatchPerFile;
                foreach (var choice in _savedChoices.Values)
                    choice.IsChecked = s.CheckedSavedPatterns.Contains(choice.Name);
                foreach (string p in s.CustomPatterns)
                    AddCustomPattern(p, s.CheckedCustomPatterns.Contains(p));
                ModeChip(s.Mode).IsChecked = true;
                _exportFormat = s.ExportFormat;
            }
            finally
            {
                _applyingSettings = false;
            }
            SetMode(s.Mode);
            UpdateOptionHints();
        }

        private void SaveSettings() => FileSearchManager.SaveSettings(new FileSearchSettings
        {
            Mode = _mode,
            SearchText = TxtSearchText.Text,
            SearchFileNames = ChkSearchFileNames.IsChecked == true,
            SearchFileContents = ChkSearchFileContents.IsChecked == true,
            IgnoreCase = ChkCaseSensitive.IsChecked != true,
            WholeWord = ChkWholeWordOnly.IsChecked == true,
            IncludeSubfolders = InputCard.IncludeSubfolders,
            UniqueValues = ChkUniqueMatchesOnly.IsChecked == true,
            FirstMatchPerFile = ChkRegexFirstMatchOnly.IsChecked == true,
            CheckedSavedPatterns = _patterns.Where(p => p.IsSaved && p.IsChecked).Select(p => p.Name).ToList(),
            CustomPatterns = _patterns.Where(p => !p.IsSaved).Select(p => p.Pattern).ToList(),
            CheckedCustomPatterns = _patterns.Where(p => !p.IsSaved && p.IsChecked).Select(p => p.Pattern).ToList(),
            ExportFormat = _exportFormat,
        });

        private void QueueSave()
        {
            if (Ignore) return;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void Option_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore) return;
            UpdateOptionHints();
            HideSearchError();
            QueueSave();
        }

        private void UpdateOptionHints()
        {
            bool unique = ChkUniqueMatchesOnly.IsChecked == true;
            bool several = _mode == FileSearchMode.Regex && SelectedPatterns().Count > 1;
            TxtFirstMatchHint.Text = unique
                ? (several
                    ? "Each file shows, for every ticked pattern, its first value that isn't listed yet. The whole file is still read, so no value is missed."
                    : "Each file shows its first value that isn't listed yet. The whole file is still read, so no value is missed.")
                : (several
                    ? "One row per ticked pattern: a file shows its first email AND its first phone, for example - then stops."
                    : "Stops reading a file at its first match.");
            ChkWholeWordOnly.IsEnabled = _mode == FileSearchMode.Text;
        }

        // =================================================================
        //  Mode: text / wildcard / regex
        // =================================================================

        private RadioButton ModeChip(FileSearchMode mode) => mode switch
        {
            FileSearchMode.Wildcard => RadioModeWildcard,
            FileSearchMode.Regex => RadioModeRegex,
            _ => RadioModeText,
        };

        private void Mode_Click(object sender, RoutedEventArgs e)
        {
            if (Ignore || sender is not RadioButton { Tag: string tag } || !Enum.TryParse(tag, out FileSearchMode mode)) return;
            if (_isSearching)
            {
                ModeChip(_mode).IsChecked = true;   // the running search keeps its settings
                return;
            }
            SetMode(mode);
            QueueSave();
        }

        private void SetMode(FileSearchMode mode)
        {
            _mode = mode;
            bool regex = mode == FileSearchMode.Regex;
            if (regex != (PanelRegexOptions.Visibility == Visibility.Visible)) UiMotion.FadeVisibility(PanelRegexOptions, regex);
            PanelRegexInput.Visibility = regex ? Visibility.Visible : Visibility.Collapsed;
            PanelSearchText.Visibility = regex ? Visibility.Collapsed : Visibility.Visible;
            IconSearchText.Kind = mode == FileSearchMode.Wildcard ? PackIconKind.Asterisk : PackIconKind.Magnify;
            TxtSearchText.Tag = mode == FileSearchMode.Wildcard ? "e.g. *.log or invoice-????.pdf" : "What are you looking for?";
            TxtModeHint.Text = mode switch
            {
                FileSearchMode.Wildcard => "* = any characters, ? = one character. Matches a whole file name (or a whole line).",
                FileSearchMode.Regex => "Tick one or more patterns below - all of them are searched at once.",
                _ => "Finds the text anywhere in file names and lines.",
            };
            HideSearchError();
            UpdateOptionHints();
            UpdatePatternCount();
            ValidateRegexPattern();
        }

        private void TxtSearchText_TextChanged(object sender, TextChangedEventArgs e)
        {
            HideSearchError();
            QueueSave();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            BtnSearchFiles_Click(sender, e);
            e.Handled = true;
        }

        // =================================================================
        //  Patterns: saved ones (from Settings) and typed ones, several ticked at once
        // =================================================================

        private void SubscribeSavedPatterns()
        {
            if (_subscribed) return;
            _subscribed = true;
            AppSettingsStore.SavedPatterns.CollectionChanged += SavedPatterns_CollectionChanged;
            SyncSavedPatterns();
        }

        private void UnsubscribeSavedPatterns()
        {
            if (!_subscribed) return;
            _subscribed = false;
            AppSettingsStore.SavedPatterns.CollectionChanged -= SavedPatterns_CollectionChanged;
            foreach (var saved in _savedChoices.Keys) saved.PropertyChanged -= SavedPattern_PropertyChanged;
        }

        private void SavedPatterns_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncSavedPatterns();

        /// <summary>Rebuilds the saved part of the list, keeping each pattern's tick.</summary>
        private void SyncSavedPatterns()
        {
            var current = AppSettingsStore.SavedPatterns.ToList();
            foreach (var gone in _savedChoices.Keys.Except(current).ToList())
            {
                gone.PropertyChanged -= SavedPattern_PropertyChanged;
                _patterns.Remove(_savedChoices[gone]);
                _savedChoices.Remove(gone);
            }
            for (int i = 0; i < current.Count; i++)
            {
                var saved = current[i];
                if (!_savedChoices.TryGetValue(saved, out var choice))
                {
                    choice = new FileSearchPatternChoice { IsSaved = true, IsWildcard = IsWildcard(saved), Name = saved.Name, Pattern = saved.Pattern };
                    choice.Error = FileSearchManager.CheckPattern(choice.Pattern, choice.IsWildcard);
                    choice.PropertyChanged += Choice_PropertyChanged;
                    _savedChoices[saved] = choice;
                    saved.PropertyChanged += SavedPattern_PropertyChanged;
                    _patterns.Insert(Math.Min(i, _patterns.Count), choice);
                }
                else
                {
                    // Settings may have reordered: keep saved patterns first, in Settings' order.
                    int at = _patterns.IndexOf(choice);
                    if (at != i && i < _patterns.Count) _patterns.Move(at, i);
                }
            }
            UpdatePatternCount();
        }

        private static bool IsWildcard(SavedSearchPattern p) => string.Equals(p.Type, "Wildcard", StringComparison.OrdinalIgnoreCase);

        /// <summary>A saved pattern edited in Settings: follow the edit (a changed type is a new entry).</summary>
        private void SavedPattern_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not SavedSearchPattern saved || !_savedChoices.TryGetValue(saved, out var choice)) return;
            switch (e.PropertyName)
            {
                case nameof(SavedSearchPattern.Name):
                    choice.Name = saved.Name;
                    QueueSave();
                    break;
                case nameof(SavedSearchPattern.Pattern):
                    choice.Pattern = saved.Pattern;
                    choice.Error = FileSearchManager.CheckPattern(choice.Pattern, choice.IsWildcard);
                    UpdatePatternCount();
                    break;
                case nameof(SavedSearchPattern.Type) when IsWildcard(saved) != choice.IsWildcard:
                    bool wasChecked = choice.IsChecked;
                    saved.PropertyChanged -= SavedPattern_PropertyChanged;
                    _patterns.Remove(choice);
                    _savedChoices.Remove(saved);
                    SyncSavedPatterns();
                    if (_savedChoices.TryGetValue(saved, out var fresh)) fresh.IsChecked = wasChecked;
                    break;
            }
        }

        private void Choice_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(FileSearchPatternChoice.IsChecked)) return;
            UpdatePatternCount();
            UpdateOptionHints();
            HideSearchError();
            QueueSave();
        }

        private FileSearchPatternChoice? AddCustomPattern(string pattern, bool isChecked)
        {
            var existing = _patterns.FirstOrDefault(p => !p.IsWildcard && p.Pattern == pattern);
            if (existing != null)
            {
                existing.IsChecked = true;
                return existing;
            }
            var choice = new FileSearchPatternChoice
            {
                IsSaved = false,
                Name = pattern.Length <= 28 ? pattern : pattern[..27] + "…",
                Pattern = pattern,
                IsChecked = isChecked,
                Error = FileSearchManager.CheckPattern(pattern, false),
            };
            choice.PropertyChanged += Choice_PropertyChanged;
            _patterns.Add(choice);
            return choice;
        }

        private void BtnAddRegexPattern_Click(object sender, RoutedEventArgs e)
        {
            string pattern = TxtRegexPattern.Text;
            if (string.IsNullOrWhiteSpace(pattern)) return;
            string? error = FileSearchManager.CheckPattern(pattern, false);
            if (error != null)
            {
                ShowSearchError("Can't add it - the pattern isn't valid: " + error);
                return;
            }
            AddCustomPattern(pattern, true);
            TxtRegexPattern.Clear();
            UiMotion.Pop(LstRegexPatterns, 0.98);
            QueueSave();
        }

        private void TxtRegexPattern_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (TxtRegexPattern.Text.Length > 0) BtnAddRegexPattern_Click(sender, e);
            else BtnSearchFiles_Click(sender, e);
        }

        private void BtnRemovePattern_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: FileSearchPatternChoice choice } || choice.IsSaved) return;
            choice.PropertyChanged -= Choice_PropertyChanged;
            _patterns.Remove(choice);
            QueueSave();
        }

        private void BtnTickAllPatterns_Click(object sender, RoutedEventArgs e)
        {
            foreach (var p in _patterns.Where(p => !p.HasError)) p.IsChecked = true;
        }

        private void BtnUntickAllPatterns_Click(object sender, RoutedEventArgs e)
        {
            foreach (var p in _patterns) p.IsChecked = false;
        }

        private void TxtRegexPattern_TextChanged(object sender, TextChangedEventArgs e)
        {
            HideSearchError();
            ValidateRegexPattern();
        }

        private void ValidateRegexPattern()
        {
            string pattern = TxtRegexPattern.Text;
            bool show = _mode == FileSearchMode.Regex && pattern.Length > 0;
            TxtRegexValidation.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            string? error = FileSearchManager.CheckPattern(pattern, false);
            TxtRegexValidation.Text = error == null ? "✓ Valid - it's searched together with the ticked patterns (Enter adds it to the list)." : "Not valid yet: " + error;
            TxtRegexValidation.SetResourceReference(TextBlock.ForegroundProperty, error == null ? "StatusSuccessFg" : "StatusErrorFg");
        }

        private List<FileSearchPattern> SelectedPatterns()
        {
            var list = _patterns.Where(p => p.IsChecked && !p.HasError)
                                .Select(p => new FileSearchPattern(p.Name, p.Pattern, p.IsWildcard))
                                .ToList();
            string typed = TxtRegexPattern.Text;
            if (typed.Length > 0 && FileSearchManager.CheckPattern(typed, false) == null && !list.Any(p => !p.IsWildcard && p.Pattern == typed))
                list.Add(new FileSearchPattern("Typed", typed));
            return list;
        }

        private void UpdatePatternCount()
        {
            int ticked = _patterns.Count(p => p.IsChecked && !p.HasError);
            int broken = _patterns.Count(p => p.HasError);
            TxtPatternCount.Text = (ticked == 0 ? "No pattern ticked" : $"{ticked} {FileSearchText.Plural(ticked, "pattern", "patterns")} ticked")
                                 + (broken > 0 ? $" · {broken} can't be used (fix in Settings)" : "");
        }

        // =================================================================
        //  Folders
        // =================================================================

        private void AddFolders(IEnumerable<string> paths)
        {
            int added = 0;
            foreach (string path in paths)
            {
                if (!Directory.Exists(path) || _folders.Any(f => string.Equals(f.FullPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                _folders.Add(new FileSearchFolder(path));
                added++;
            }
            if (added > 0)
            {
                InputCard.PopDropArea();
                HideSearchError();
            }
        }

        private void BtnRemoveSingleFolder_Click(object sender, RoutedEventArgs e)
        {
            if (!_isSearching && sender is Button { Tag: FileSearchFolder folder }) _folders.Remove(folder);
        }

        private void InputCard_PathsAdded(object? sender, InputPathsEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (!_isSearching) AddFolders(e.Paths);
        }

        private void InputCard_IncludeSubfoldersChanged(object? sender, EventArgs e) => Option_Click(sender ?? this, new RoutedEventArgs());

        private void InputCard_ClearRequested(object? sender, EventArgs e)
        {
            if (_isSearching || _folders.Count == 0) return;
            _folders.Clear();
        }

        private void UpdateFolderState()
        {
            InputCard.SetEmpty(_folders.Count == 0);
        }

        private void Root_DragOver(object sender, DragEventArgs e)
        {
            bool ok = !_isSearching && e.Data.GetDataPresent(DataFormats.FileDrop);
            if (!ok) return;   // text dragged into a box behaves as usual
            e.Effects = DragDropEffects.Copy;
            if (DropOverlay.Visibility != Visibility.Visible) UiMotion.FadeVisibility(DropOverlay, true);
            e.Handled = true;
        }

        /// <summary>DragLeave also fires when moving between child elements - only hide once the pointer is really outside.</summary>
        private void Root_DragLeave(object sender, DragEventArgs e)
        {
            var p = e.GetPosition(this);
            if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) DropOverlay.Visibility = Visibility.Collapsed;
        }

        private void Root_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (_isSearching || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
            e.Handled = true;
            AddFolders(paths.Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p) ?? "").Where(p => p.Length > 0));
        }

        // =================================================================
        //  Search
        // =================================================================

        private FileSearchOptions BuildOptions() => new()
        {
            Folders = _folders.Select(f => f.FullPath).ToList(),
            Mode = _mode,
            SearchText = TxtSearchText.Text,
            Patterns = _mode == FileSearchMode.Regex ? SelectedPatterns() : Array.Empty<FileSearchPattern>(),
            SearchFileNames = ChkSearchFileNames.IsChecked == true,
            SearchFileContents = ChkSearchFileContents.IsChecked == true,
            IgnoreCase = ChkCaseSensitive.IsChecked != true,
            WholeWord = _mode == FileSearchMode.Text && ChkWholeWordOnly.IsChecked == true,
            IncludeSubfolders = InputCard.IncludeSubfolders,
            UniqueValues = ChkUniqueMatchesOnly.IsChecked == true,
            FirstMatchPerFile = ChkRegexFirstMatchOnly.IsChecked == true,
        };

        private async void BtnSearchFiles_Click(object sender, RoutedEventArgs e)
        {
            if (_isSearching)
            {
                _searchCts?.Cancel();
                return;
            }

            var options = BuildOptions();
            string? problem = options.Folders.Count == 0 ? "Add at least one folder to search."
                : !options.SearchFileNames && !options.SearchFileContents ? "Turn on Search file names and/or Search file contents."
                : null;
            if (problem == null) FileSearchManager.Matcher.Build(options, out problem);
            if (problem != null)
            {
                ShowSearchError(problem);
                if (options.Folders.Count == 0) InputCard.PopDropArea();
                return;
            }
            HideSearchError();

            _isSearching = true;
            _searchCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _lastWasUnique = options.UniqueValues;
            _lastSummary = null;
            _results.Clear();
            ColSeen.Visibility = options.UniqueValues ? Visibility.Visible : Visibility.Collapsed;
            ColPattern.Visibility = options.Mode == FileSearchMode.Regex && options.Patterns.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            SetSearchingUi(true);
            ProgressSearch.IsIndeterminate = true;
            ProgressSearch.Value = 0;
            TxtProgressPercent.Text = "";
            TxtProgress.Text = "Finding files…";
            UpdateResultState();

            try
            {
                var progress = new Progress<FileSearchProgress>(OnProgress);
                var summary = await FileSearchManager.SearchAsync(options, progress, _searchCts.Token);
                if (_lifetimeCts.IsCancellationRequested) return;
                _lastSummary = summary;
                ProgressSearch.IsIndeterminate = false;
                if (!summary.Cancelled) ProgressSearch.Value = 100;
                TxtProgressPercent.Text = $"{ProgressSearch.Value:0}%";
                TxtProgress.Text = FileSearchManager.Describe(summary, options.UniqueValues);
                ShowSummaryPills(summary);
                UpdateResultState();
                if (summary.Elapsed > TimeSpan.FromSeconds(8) && !summary.Cancelled)
                    Toast("Search complete", FileSearchManager.Describe(summary, options.UniqueValues));
            }
            catch (ArgumentException ex)
            {
                ShowSearchError(ex.Message);
            }
            finally
            {
                _isSearching = false;
                ProgressSearch.IsIndeterminate = false;
                _searchCts?.Dispose();
                _searchCts = null;
                SetSearchingUi(false);
            }
        }

        private void OnProgress(FileSearchProgress p)
        {
            if (!_isSearching) return;
            if (p.StillListing)
            {
                TxtProgress.Text = $"Finding files… {p.TotalFiles:N0} so far";
                return;
            }
            ProgressSearch.IsIndeterminate = false;
            if (p.NewResults.Count > 0) _results.AddRange(p.NewResults);
            foreach (var u in p.SeenUpdates) u.Row.ApplySeen(u.Occurrences, u.FileCount, u.OtherFiles);
            ProgressSearch.Value = p.Percent;
            TxtProgressPercent.Text = $"{p.Percent:0}%";
            string found = _lastWasUnique
                ? $"{p.Results:N0} different {FileSearchText.Plural(p.Results, "value", "values")}"
                : $"{p.Results:N0} {FileSearchText.Plural(p.Results, "match", "matches")}";
            TxtProgress.Text = $"{p.FilesScanned:N0} of {p.TotalFiles:N0} files · {found}" + (p.CurrentFile.Length > 0 ? " · " + p.CurrentFile : "");
            if (p.NewResults.Count > 0) UpdateResultState();
        }

        private void SetSearchingUi(bool searching)
        {
            IconSearch.Kind = searching ? PackIconKind.Stop : PackIconKind.FileSearchOutline;
            if (searching) TxtSearchLabel.Text = "Cancel";
            else TxtSearchLabel.SetResourceReference(TextBlock.TextProperty, "Str_Search_RunSearchButton");
            OptionsScrollViewer.IsEnabled = !searching;   // the search uses what was set when it started
            PanelSearchText.IsEnabled = !searching;
            PanelRegexInput.IsEnabled = !searching;
            UpdateResultState();
        }

        private void ShowSummaryPills(FileSearchSummary s)
        {
            TxtFilesCount.Text = $"{s.FilesWithMatches:N0} of {s.FilesScanned:N0} {FileSearchText.Plural(s.FilesScanned, "file", "files")}";
            int skipped = s.Skipped.Count + s.InaccessibleFolders;
            PillSkipped.Visibility = skipped > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtSkippedCount.Text = $"{skipped:N0} skipped";
            var lines = s.Skipped.Take(25).Select(x => $"{Path.GetFileName(x.Path)}: {x.Reason}").ToList();
            if (s.Skipped.Count > 25) lines.Add($"… and {s.Skipped.Count - 25:N0} more");
            if (s.InaccessibleFolders > 0) lines.Add($"{s.InaccessibleFolders:N0} {FileSearchText.Plural(s.InaccessibleFolders, "folder", "folders")} couldn't be opened (no permission)");
            PillSkipped.ToolTip = lines.Count == 0 ? null : string.Join("\n", lines);
            UiMotion.Pop(PillResults, 0.9);
        }

        /// <summary>Pills, the empty state and the buttons that need rows.</summary>
        private void UpdateResultState()
        {
            int total = _results.Count;
            int shown = _filter.Length == 0 ? total : _resultsView.Cast<object>().Count();
            bool any = total > 0;

            DataGridResults.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            if (any == (EmptyStatePanel.Visibility == Visibility.Visible)) UiMotion.FadeVisibility(EmptyStatePanel, !any);
            if (!any && _lastSummary != null && !_isSearching)
            {
                EmptyStatePanel.Title = _lastSummary.Cancelled ? "Stopped before anything was found" : "Nothing found";
                EmptyStatePanel.Description = _lastSummary.FilesScanned == 0
                    ? "No files were searched - check that the folders still exist."
                    : $"{_lastSummary.FilesScanned:N0} {FileSearchText.Plural(_lastSummary.FilesScanned, "file was", "files were")} searched. Try turning off Match case, or check the pattern.";
                UiMotion.Pop(EmptyStatePanel, 0.9);
            }

            SummaryPanel.Visibility = any || _lastSummary != null ? Visibility.Visible : Visibility.Collapsed;
            string noun = _lastWasUnique ? FileSearchText.Plural(total, "value", "values") : FileSearchText.Plural(total, "match", "matches");
            TxtResultCount.Text = shown == total ? $"{total:N0} {noun}" : $"{shown:N0} of {total:N0} {noun}";
            PillFiles.Visibility = _lastSummary != null ? Visibility.Visible : Visibility.Collapsed;
            if (_lastSummary == null) PillSkipped.Visibility = Visibility.Collapsed;

            BtnCopyValues.IsEnabled = shown > 0;
            BtnExportResults.IsEnabled = !_isSearching && any;
            BtnClearResults.IsEnabled = !_isSearching && any;
        }

        private void TxtResultFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            _filterTimer.Stop();
            _filterTimer.Start();
        }

        private void ShowSearchError(string message)
        {
            TxtSearchError.Text = message;
            if (SearchErrorBanner.Visibility != Visibility.Visible) UiMotion.FadeVisibility(SearchErrorBanner, true);
            else UiMotion.Pop(SearchErrorBanner, 0.97);
        }

        private void HideSearchError()
        {
            if (SearchErrorBanner.Visibility == Visibility.Visible) UiMotion.FadeVisibility(SearchErrorBanner, false);
        }

        // =================================================================
        //  Results: clear, copy, export, open
        // =================================================================

        private void BtnClearResults_Click(object sender, RoutedEventArgs e)
        {
            if (_isSearching) return;
            _results.Clear();
            _lastSummary = null;
            ProgressSearch.Value = 0;
            TxtProgressPercent.Text = "";
            TxtProgress.SetResourceReference(TextBlock.TextProperty, "Str_Search_ProgressInitial");
            EmptyStatePanel.SetResourceReference(Controls.EmptyStateCard.TitleProperty, "Str_SearchFile_FindTextCodesAnd");
            EmptyStatePanel.SetResourceReference(Controls.EmptyStateCard.DescriptionProperty, "Str_SearchFile_AddOneOrMore");
            UpdateResultState();
        }

        private IEnumerable<FileSearchResult> VisibleRows() => _resultsView.Cast<FileSearchResult>();

        private void BtnCopyValues_Click(object sender, RoutedEventArgs e)
        {
            string text = FileSearchManager.ValuesText(VisibleRows());
            if (text.Length > 0 && TrySetClipboard(text))
            {
                int count = text.Count(c => c == '\n');
                TxtProgress.Text = $"Copied {count:N0} {FileSearchText.Plural(count, "value", "values")}.";
                UiMotion.Pop(BtnCopyValues, 0.85);
            }
        }

        /// <summary>Export opens a menu: CSV, text with just the values, or a text report. The last choice is ticked.</summary>
        private void BtnExportResults_Click(object sender, RoutedEventArgs e)
        {
            if (!VisibleRows().Any()) return;
            MenuExportCsv.IsChecked = _exportFormat == FileSearchExportFormat.Csv;
            MenuExportValues.IsChecked = _exportFormat == FileSearchExportFormat.TextValues;
            MenuExportReport.IsChecked = _exportFormat == FileSearchExportFormat.TextReport;
        }

        private async void MenuExport_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse(tag, out FileSearchExportFormat format)) return;
            _exportFormat = format;
            QueueSave();
            await ExportResultsAsync(format);
        }

        private async Task ExportResultsAsync(FileSearchExportFormat format)
        {
            var rows = VisibleRows().ToList();
            if (rows.Count == 0) return;
            string filter = format switch
            {
                FileSearchExportFormat.TextValues => "Text file - values only (*.txt)|*.txt",
                FileSearchExportFormat.TextReport => "Text file - report (*.txt)|*.txt",
                _ => "CSV file (*.csv)|*.csv",
            };
            var dialog = new SaveFileDialog
            {
                Title = "Export search results",
                Filter = filter,
                FileName = FileSearchManager.ExportFileName(format, DateTime.Now),   // dated, so each export is its own file
                AddExtension = true,
                OverwritePrompt = false,   // nothing is ever replaced: a taken name gets the date and time added
            };
            if (dialog.ShowDialog() != true) return;

            BtnExportResults.IsEnabled = false;
            try
            {
                string saved = await FileSearchManager.ExportAsync(rows, dialog.FileName, _lastWasUnique, format);
                string what = format == FileSearchExportFormat.TextValues
                    ? "the values"
                    : $"{rows.Count:N0} {FileSearchText.Plural(rows.Count, "row", "rows")}";
                bool renamed = !string.Equals(saved, dialog.FileName, StringComparison.OrdinalIgnoreCase);
                TxtProgress.Text = $"Exported {what} to {Path.GetFileName(saved)}.";
                string note = renamed ? $"\n\n{Path.GetFileName(dialog.FileName)} already exists, so it was kept and this export got its own name." : "";
                var open = MessageBox.Show(Loc.F("Str_MB_ExportedToNN", "Exported {0} to:\n{1}{2}\n\nOpen the file now?", what, saved, note), Loc.T("Str_Dlg_ExportComplete", "Export complete"), MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (open == MessageBoxResult.Yes) OpenWithShell(saved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(Loc.T("Str_MB_CouldnTExport2", "Couldn't export: ") + FileSearchManager.PlainSaveError(ex), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateResultState();
            }
        }

        private FileSearchResult? SelectedRow => DataGridResults.SelectedItem as FileSearchResult;

        /// <summary>Double-click a row: show the file in its folder (only on a row, not the header or scrollbar).</summary>
        private void DataGridResults_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            while (source != null && source is not DataGridRow) source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            if (source is DataGridRow { Item: FileSearchResult row }) ShowInFolder(row.FullPath);
        }

        private void MenuCopyMatch_Click(object sender, RoutedEventArgs e)
        {
            var rows = DataGridResults.SelectedItems.OfType<FileSearchResult>().ToList();
            if (rows.Count > 0) TrySetClipboard(FileSearchManager.ValuesText(rows).TrimEnd());
        }

        private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
        {
            var paths = DataGridResults.SelectedItems.OfType<FileSearchResult>().Select(r => r.FullPath).Distinct().ToList();
            if (paths.Count > 0) TrySetClipboard(string.Join(Environment.NewLine, paths));
        }

        private void MenuOpenFile_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRow is { } row) OpenWithShell(row.FullPath);
        }

        private void MenuShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRow is { } row) ShowInFolder(row.FullPath);
        }

        private static void ShowInFolder(string path)
        {
            if (File.Exists(path) && !Smart_Tools.WPF.Controllers.ShellOpen.Reveal(path))
                MessageBox.Show(Loc.T("Str_MB_CouldnTOpenThe", "Couldn't open the folder."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>Opens the file in its default program. Programs and scripts are shown in Explorer instead of being run.</summary>
        private static void OpenWithShell(string path)
        {
            if (!Smart_Tools.WPF.Controllers.ShellOpen.OpenFile(path))
                MessageBox.Show(Loc.T("Str_MB_CouldnTOpenThe2", "Couldn't open the file."), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static bool TrySetClipboard(string text)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;   // another program has the clipboard open
            }
        }

        private static void Toast(string title, string message)
        {
            try
            {
                ToastNotificationManager.ShowToast(title, message);
            }
            catch (Exception)
            {
                // a toast is a nice-to-have
            }
        }
    }
}
