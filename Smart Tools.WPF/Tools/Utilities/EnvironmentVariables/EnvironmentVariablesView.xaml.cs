using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Panel = System.Windows.Controls.Panel;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Environment Variables Editor - UI only. Reading and writing the registry, undo, and the PATH checks are in
    /// <see cref="EnvironmentVariablesManager"/>. Edits stay in the editor until Save; deleting and changing system
    /// variables ask first.
    /// </summary>
    public partial class EnvironmentVariablesView : UserControl
    {
        private const double OptionsExpandedWidth = 280;
        private const double OptionsCollapsedWidth = 56;
        private static string DialogTitle => Loc.T("Str_Tool_environment-variables_Name", "Environment Variables");

        private readonly EnvironmentVariablesManager _env = new();
        private readonly DispatcherTimer _pathTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly bool _viewConstructed;
        private List<EnvVariable> _vars = new();
        private EnvScope _scope = EnvScope.User;
        private bool _isNew, _dirty, _loadingEditor, _optionsCollapsed, _loadedOnce;
        private bool _suppressSelection;

        public EnvironmentVariablesView()
        {
            InitializeComponent();
            _pathTimer.Tick += (_, _) =>
            {
                _pathTimer.Stop();
                RebuildPathList();
            };
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing. Nothing is held open; unsaved edits are simply not written.</summary>
        public void CancelActiveRun() => _pathTimer.Stop();

        private static string T(string key, string fallback) => Application.Current?.TryFindResource(key) as string ?? fallback;

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            UpdateScopeUi();
            Reload(null);
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
        //  Scope and list
        // =================================================================

        private void Scope_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || sender is not RadioButton { Tag: string tag } button) return;
            var wanted = Enum.Parse<EnvScope>(tag);
            if (wanted == _scope) return;

            if (!ConfirmDiscard())
            {
                // Put the toggle back on the scope that is still showing.
                if (button.Parent is Panel panel)
                    foreach (var other in panel.Children.OfType<RadioButton>()) other.IsChecked = (string?)other.Tag == _scope.ToString();
                return;
            }
            _scope = wanted;
            UpdateScopeUi();
            Reload(null);
        }

        private void UpdateScopeUi()
        {
            bool system = _scope == EnvScope.System;
            TxtScopeHint.Text = system
                ? T("Str_EnvVars_ScopeHintSystem", "Variables for every account on this PC. Changing them needs administrator rights.")
                : T("Str_EnvVars_ScopeHintUser", "Variables for your account only.");
            AdminBanner.Visibility = system && !EnvironmentVariablesManager.IsAdministrator ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Re-reads the registry; <paramref name="select"/> picks a variable to open afterwards (by name).</summary>
        private void Reload(string? select)
        {
            _vars = _env.Load(_scope);
            _isNew = false;   // every caller has saved, deleted, or agreed to discard what was open
            _dirty = false;
            ApplyFilter(select);
            BtnUndo.IsEnabled = _env.CanUndo;
        }

        private List<EnvVariable> Filtered()
        {
            string query = TxtSearch.Text.Trim();
            return _vars.Where(v => query.Length == 0 ||
                v.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                v.Value.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }

        private void ApplyFilter(string? select)
        {
            var shown = Filtered();

            _suppressSelection = true;
            try
            {
                GridVars.ItemsSource = shown;
                var pick = select == null ? null : shown.FirstOrDefault(v => v.Name.Equals(select, StringComparison.OrdinalIgnoreCase));
                GridVars.SelectedItem = pick;
                if (pick != null) GridVars.ScrollIntoView(pick);
            }
            finally
            {
                _suppressSelection = false;
            }

            if (GridVars.SelectedItem is EnvVariable current) LoadEditor(current, isNew: false);
            else ShowNoSelection();
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_viewConstructed || !_loadedOnce) return;
            string? keep = _isNew || PanelEditor.Visibility != Visibility.Visible ? null : TxtName.Text;
            ApplyFilterKeepingEditor(keep);
        }

        /// <summary>Filtering must not close an editor with unsaved edits: only the list changes.</summary>
        private void ApplyFilterKeepingEditor(string? selectedName)
        {
            var shown = Filtered();
            _suppressSelection = true;
            try
            {
                GridVars.ItemsSource = shown;
                GridVars.SelectedItem = selectedName == null ? null : shown.FirstOrDefault(v => v.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                _suppressSelection = false;
            }
        }

        private void GridVars_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_viewConstructed || _suppressSelection) return;
            var next = GridVars.SelectedItem as EnvVariable;
            if (next == null) return;

            bool sameAsOpen = !_isNew && PanelEditor.Visibility == Visibility.Visible && next.Name.Equals(TxtName.Text, StringComparison.OrdinalIgnoreCase);
            if (sameAsOpen) return;

            if (!ConfirmDiscard())
            {
                _suppressSelection = true;
                GridVars.SelectedItem = _isNew ? null : _vars.FirstOrDefault(v => v.Name.Equals(TxtName.Text, StringComparison.OrdinalIgnoreCase));
                _suppressSelection = false;
                return;
            }
            LoadEditor(next, isNew: false);
        }

        private bool ConfirmDiscard() =>
            !_dirty || AppModal.Show(T("Str_EnvVars_DiscardConfirm", "Discard the changes you haven't saved?"), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        // =================================================================
        //  Editor
        // =================================================================

        private void ShowNoSelection()
        {
            PanelEditor.Visibility = Visibility.Collapsed;
            PanelNoSelection.Visibility = Visibility.Visible;
            _isNew = false;
            _dirty = false;
        }

        private void LoadEditor(EnvVariable variable, bool isNew)
        {
            _loadingEditor = true;
            try
            {
                _isNew = isNew;
                TxtName.Text = variable.Name;
                TxtName.IsReadOnly = !isNew;
                TxtValue.Text = variable.Value;
                ChkExpand.IsChecked = variable.IsExpandable;
                BtnDelete.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
                HdrEditor.Content = isNew ? T("Str_EnvVars_NewHeader", "New variable") : T("Str_EnvVars_EditHeader", "Edit variable");
                SetStatus(null);
                _dirty = false;
                PanelEditor.Visibility = Visibility.Visible;
                PanelNoSelection.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _loadingEditor = false;
            }
            UpdateAfterValueChange(immediate: true);
        }

        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDiscard()) return;
            _suppressSelection = true;
            GridVars.SelectedItem = null;
            _suppressSelection = false;
            LoadEditor(new EnvVariable { Scope = _scope }, isNew: true);
            TxtName.Focus();
        }

        private void Editor_Changed(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed || _loadingEditor) return;
            _dirty = true;
            SetStatus(null);
            UpdateAfterValueChange(immediate: false);
        }

        private void TxtValue_TextChanged(object sender, TextChangedEventArgs e) => Editor_Changed(sender, e);

        /// <summary>The %reference% hint and the PATH list follow the name and value; the list waits a moment while typing.</summary>
        private void UpdateAfterValueChange(bool immediate)
        {
            bool needsExpand = EnvironmentVariablesManager.HasExpandToken(TxtValue.Text) && ChkExpand.IsChecked != true;
            TxtExpandHint.Text = needsExpand ? T("Str_EnvVars_ExpandHint", "This value refers to other variables with %NAME%. Turn on \"expand\" so Windows replaces them; otherwise they are used as typed.") : "";
            TxtExpandHint.Visibility = needsExpand ? Visibility.Visible : Visibility.Collapsed;

            bool isPath = EnvironmentVariablesManager.IsPathVariable(TxtName.Text.Trim());
            PathSection.Visibility = isPath ? Visibility.Visible : Visibility.Collapsed;
            _pathTimer.Stop();
            if (!isPath) return;
            if (immediate) RebuildPathList();
            else _pathTimer.Start();
        }

        // =================================================================
        //  PATH folders
        // =================================================================

        private void RebuildPathList()
        {
            var entries = EnvironmentVariablesManager.Analyze(EnvironmentVariablesManager.SplitPath(TxtValue.Text), EnvironmentVariablesManager.DirectoryExists);
            foreach (var entry in entries) entry.IssueText = IssueText(entry.Issue);
            ListPath.ItemsSource = entries;

            int problems = entries.Count(en => en.HasIssue);
            TxtPathIssues.Text = problems == 0
                ? string.Format(T("Str_EnvVars_PathOk", "{0} folders, no problems"), entries.Count)
                : string.Format(T("Str_EnvVars_PathIssues", "{0} folders, {1} to check"), entries.Count, problems);
            string kind = problems == 0 ? "Success" : "Warning";
            PillPathIssues.SetResourceReference(Border.BackgroundProperty, $"Status{kind}Bg");
            IconPathIssues.SetResourceReference(PackIcon.ForegroundProperty, $"Status{kind}Fg");
            TxtPathIssues.SetResourceReference(TextBlock.ForegroundProperty, $"Status{kind}Fg");
            IconPathIssues.Kind = problems == 0 ? PackIconKind.CheckCircleOutline : PackIconKind.AlertCircleOutline;
            BtnPathClean.IsEnabled = entries.Any(en => en.Issue is EnvPathIssue.Empty or EnvPathIssue.Duplicate or EnvPathIssue.Missing);
        }

        private static string IssueText(EnvPathIssue issue) => issue switch
        {
            EnvPathIssue.Empty => T("Str_EnvVars_IssueEmpty", "Empty entry (a stray semicolon)."),
            EnvPathIssue.Duplicate => T("Str_EnvVars_IssueDuplicate", "Already listed earlier, so this one does nothing."),
            EnvPathIssue.Relative => T("Str_EnvVars_IssueRelative", "Not a full path, so it depends on where a program starts."),
            EnvPathIssue.Missing => T("Str_EnvVars_IssueMissing", "This folder doesn't exist."),
            _ => "",
        };

        private List<string> CurrentFolders() => EnvironmentVariablesManager.SplitPath(TxtValue.Text);

        private void SetFolders(IEnumerable<string> folders)
        {
            TxtValue.Text = EnvironmentVariablesManager.JoinPath(folders);   // marks the editor changed
            _pathTimer.Stop();
            RebuildPathList();
        }

        private int IndexOf(object sender) => (sender as FrameworkElement)?.DataContext is EnvPathEntry entry ? ListPath.Items.IndexOf(entry) : -1;

        private void BtnPathUp_Click(object sender, RoutedEventArgs e)
        {
            int i = IndexOf(sender);
            var folders = CurrentFolders();
            if (i <= 0 || i >= folders.Count) return;
            (folders[i - 1], folders[i]) = (folders[i], folders[i - 1]);
            SetFolders(folders);
        }

        private void BtnPathDown_Click(object sender, RoutedEventArgs e)
        {
            int i = IndexOf(sender);
            var folders = CurrentFolders();
            if (i < 0 || i >= folders.Count - 1) return;
            (folders[i + 1], folders[i]) = (folders[i], folders[i + 1]);
            SetFolders(folders);
        }

        private void BtnPathRemove_Click(object sender, RoutedEventArgs e)
        {
            int i = IndexOf(sender);
            var folders = CurrentFolders();
            if (i < 0 || i >= folders.Count) return;
            folders.RemoveAt(i);
            SetFolders(folders);
        }

        private void BtnPathAdd_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = T("Str_EnvVars_FolderDialog", "Choose a folder to add"), UseDescriptionForTitle = true };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            var folders = CurrentFolders().Where(f => f.Length > 0).ToList();
            folders.Add(dialog.SelectedPath);
            SetFolders(folders);
        }

        private void BtnPathClean_Click(object sender, RoutedEventArgs e)
        {
            var before = CurrentFolders();
            var after = EnvironmentVariablesManager.CleanUp(before, EnvironmentVariablesManager.DirectoryExists);
            SetFolders(after);
            SetStatus(string.Format(T("Str_EnvVars_Cleaned", "Removed {0} folders. Save to apply."), before.Count - after.Count), "TextSecondary");
        }

        // =================================================================
        //  Save, delete, undo
        // =================================================================

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtName.Text.Trim(), value = TxtValue.Text;
            var problem = EnvironmentVariablesManager.CheckName(name);
            if (problem != EnvNameProblem.None)
            {
                SetStatus(problem switch
                {
                    EnvNameProblem.Empty => T("Str_EnvVars_NameEmpty", "Give the variable a name."),
                    EnvNameProblem.TooLong => T("Str_EnvVars_NameTooLong", "That name is too long."),
                    _ => T("Str_EnvVars_NameIllegal", "A name can't contain \"=\" or control characters."),
                }, "StatusErrorFg");
                TxtName.Focus();
                return;
            }
            if (_isNew && _vars.Any(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus(T("Str_EnvVars_NameExists", "A variable with that name already exists."), "StatusErrorFg");
                TxtName.Focus();
                return;
            }
            if (value.Length > EnvironmentVariablesManager.MaxValueLength)
            {
                SetStatus(T("Str_EnvVars_ValueTooLong", "The value is longer than Windows allows (32,767 characters)."), "StatusErrorFg");
                return;
            }
            if (_scope == EnvScope.System && !ConfirmSystem(name)) return;

            var result = _env.Save(_scope, name, value, ChkExpand.IsChecked == true, out string? error);
            if (result != EnvChangeResult.Done)
            {
                ReportFailure(result, error);
                return;
            }
            _dirty = false;
            _isNew = false;
            Reload(name);
            SetStatus(T("Str_EnvVars_Saved", "Saved. Programs started from now on will see it."), "StatusSuccessFg");
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtName.Text.Trim();
            if (_isNew || name.Length == 0) return;
            string question = string.Format(T("Str_EnvVars_DeleteConfirm", "Delete the variable \"{0}\"? You can undo this until you close the app."), name);
            if (AppModal.Show(question, DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (_scope == EnvScope.System && !ConfirmSystem(name)) return;

            var result = _env.Delete(_scope, name, out string? error);
            if (result != EnvChangeResult.Done)
            {
                ReportFailure(result, error);
                return;
            }
            _dirty = false;
            Reload(null);
            ShowNoSelection();
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDiscard()) return;
            var change = _env.Undo(out var result);
            if (change == null) return;
            if (result != EnvChangeResult.Done)
            {
                ReportFailure(result, null);
                return;
            }

            // The restored variable may belong to the other scope: follow it.
            if (change.Scope != _scope)
            {
                _scope = change.Scope;
                UpdateScopeUi();
            }
            _dirty = false;
            Reload(change.OldValue == null ? null : change.Name);
            if (change.OldValue == null) ShowNoSelection();
            SetStatus(string.Format(T("Str_EnvVars_Undone", "Restored {0}."), change.Name), "StatusSuccessFg");
        }

        private bool ConfirmSystem(string name) =>
            AppModal.Show(string.Format(T("Str_EnvVars_SystemConfirm", "\"{0}\" is a system variable, so this changes it for every account on this PC. Continue?"), name), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

        private void ReportFailure(EnvChangeResult result, string? error)
        {
            if (result == EnvChangeResult.NeedsAdministrator)
            {
                AdminBanner.Visibility = Visibility.Visible;
                SetStatus(T("Str_EnvVars_NeedsAdmin", "Windows only lets an administrator change system variables."), "StatusErrorFg");
            }
            else
            {
                SetStatus(string.Format(T("Str_EnvVars_Failed", "Couldn't change the variable. {0}"), error ?? ""), "StatusErrorFg");
            }
        }

        private void SetStatus(string? text, string colorKey = "TextSecondary")
        {
            TxtStatus.Text = text ?? "";
            TxtStatus.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
            TxtStatus.ToolTip = text;
        }

        /// <summary>Relaunches Smart Tools elevated (UAC prompt) and closes this instance - asks first.</summary>
        private void BtnRestartAdmin_Click(object sender, RoutedEventArgs e)
        {
            if (AppModal.Show(T("Str_EnvVars_RestartConfirm", "Restart Smart Tools as administrator? Unsaved changes in the editor and the undo history are lost."), DialogTitle, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (Window.GetWindow(this) is MainWindow main && !main.PrepareForRestart()) return;

            switch (AppRestartManager.StartElevatedCopy(out string? error))
            {
                case AppRestartResult.Started:
                    Application.Current.Shutdown();
                    break;
                case AppRestartResult.Failed:
                    AppModal.Show(string.Format(T("Str_EnvVars_RestartFailed", "Couldn't restart as administrator: {0}"), error), DialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                    break;
                // Declined: UAC said no - stay as we are.
            }
        }
    }
}
