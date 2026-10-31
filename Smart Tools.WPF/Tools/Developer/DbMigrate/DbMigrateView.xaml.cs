using MaterialDesignThemes.Wpf;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Controls;
using Smart_Tools.WPF.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Database Migrator - UI only. Connecting, reading the source, version rules, the DDL, the data copy and the
    /// verification are <see cref="DbMigrateManager"/> (and its partial files). Passwords stay in the password boxes
    /// (memory only); the settings file keeps servers, database names and user names, never secrets.
    /// </summary>
    public partial class DbMigrateView : UserControl
    {
        private const double SidebarExpandedWidth = 280;
        private const double SidebarCollapsedWidth = 56;
        private const int MaxLogLines = 5000;

        private readonly DbMigrateManager _manager = new();
        private readonly ObservableCollection<DbMigrateTableItem> _tables = new();
        private readonly ObservableCollection<DbMigrateIssue> _notes = new();
        private readonly ObservableCollection<DbMigrateLogLine> _log = new();
        private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

        private DbMigrateSettings _settings = new();
        private DbMigratePlan? _plan;
        private DbMigrateResult? _lastResult;
        private DbMigrateProgress? _lastProgress;
        private string _planSignature = "";
        private DateTime _runStart;
        private ScrollViewer? _consoleScroll;
        private bool _followLog = true;
        private bool _viewConstructed;
        private bool _loadedOnce;
        private bool _closed;
        private bool _running;
        private bool _busy;           // analyzing or running
        private bool _optionsCollapsed;

        public DbMigrateView()
        {
            InitializeComponent();
            TablesGrid.ItemsSource = _tables;
            NotesList.ItemsSource = _notes;
            ConsoleList.ItemsSource = _log;
            _manager.LogAdded += OnLog;
            _clock.Tick += (_, _) => UpdateTimePill();
            _viewConstructed = true;
        }

        /// <summary>Called from MainWindow.Window_Closing: stops a running plan/copy and saves the options.</summary>
        public void CancelActiveRun()
        {
            if (_closed) return;
            _closed = true;
            _clock.Stop();
            _manager.CancelActiveRun();
            SaveSettings();
        }

        // =================================================================
        //  Lifetime, settings, small helpers
        // =================================================================

        private void Root_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            _settings = DbMigrateManager.LoadSettings();
            ApplySettings();
            ShowTab("tables");
        }

        private string S(string key) => TryFindResource(key) as string ?? key;
        private string S(string key, params object[] args) => string.Format(S(key), args);

        private void ApplySettings()
        {
            TxtSrcServer.Text = _settings.SourceServer;
            CmbSrcDatabase.Text = _settings.SourceDatabase;
            TxtSrcUser.Text = _settings.SourceUser;
            (_settings.SourceAuth == DbMigrateAuth.Sql ? RadSrcSql : RadSrcWindows).IsChecked = true;
            ChkSrcTrust.IsChecked = _settings.SourceTrustCert;

            TxtDstServer.Text = _settings.TargetServer;
            CmbDstDatabase.Text = _settings.TargetDatabase;
            TxtDstUser.Text = _settings.TargetUser;
            (_settings.TargetAuth == DbMigrateAuth.Sql ? RadDstSql : RadDstWindows).IsChecked = true;
            ChkDstTrust.IsChecked = _settings.TargetTrustCert;
            (_settings.CreateTargetDatabase ? RadDstCreate : RadDstExisting).IsChecked = true;

            ChkData.IsChecked = _settings.IncludeData;
            ChkCode.IsChecked = _settings.IncludeCode;
            ChkIndexes.IsChecked = _settings.IncludeIndexes;
            ChkChecksum.IsChecked = _settings.VerifyChecksums;
            SldBatch.Value = Math.Clamp(_settings.BatchSize, SldBatch.Minimum, SldBatch.Maximum);
            SldParallel.Value = Math.Clamp(_settings.Parallelism, SldParallel.Minimum, SldParallel.Maximum);

            UpdateAuthPanels();
            if (_settings.OptionsCollapsed) BtnCollapseOptions_Click(this, new RoutedEventArgs());
        }

        private void SaveSettings()
        {
            if (!_loadedOnce) return;
            _settings = new DbMigrateSettings
            {
                SourceServer = TxtSrcServer.Text.Trim(),
                SourceDatabase = CmbSrcDatabase.Text.Trim(),
                SourceAuth = RadSrcSql.IsChecked == true ? DbMigrateAuth.Sql : DbMigrateAuth.Windows,
                SourceUser = TxtSrcUser.Text.Trim(),
                SourceTrustCert = ChkSrcTrust.IsChecked == true,
                TargetServer = TxtDstServer.Text.Trim(),
                TargetDatabase = CmbDstDatabase.Text.Trim(),
                TargetAuth = RadDstSql.IsChecked == true ? DbMigrateAuth.Sql : DbMigrateAuth.Windows,
                TargetUser = TxtDstUser.Text.Trim(),
                TargetTrustCert = ChkDstTrust.IsChecked == true,
                CreateTargetDatabase = RadDstCreate.IsChecked == true,
                IncludeData = ChkData.IsChecked == true,
                IncludeCode = ChkCode.IsChecked == true,
                IncludeIndexes = ChkIndexes.IsChecked == true,
                VerifyChecksums = ChkChecksum.IsChecked == true,
                BatchSize = (int)SldBatch.Value,
                Parallelism = (int)SldParallel.Value,
                OptionsCollapsed = _optionsCollapsed
            };
            DbMigrateManager.SaveSettings(_settings);
        }

        private DbMigrateEndpoint ReadEndpoint(bool source) => source
            ? new DbMigrateEndpoint
            {
                Server = TxtSrcServer.Text.Trim(),
                Database = CmbSrcDatabase.Text.Trim(),
                Auth = RadSrcSql.IsChecked == true ? DbMigrateAuth.Sql : DbMigrateAuth.Windows,
                User = TxtSrcUser.Text.Trim(),
                Password = PwdSrc.Password,
                TrustServerCertificate = ChkSrcTrust.IsChecked == true
            }
            : new DbMigrateEndpoint
            {
                Server = TxtDstServer.Text.Trim(),
                Database = CmbDstDatabase.Text.Trim(),
                Auth = RadDstSql.IsChecked == true ? DbMigrateAuth.Sql : DbMigrateAuth.Windows,
                User = TxtDstUser.Text.Trim(),
                Password = PwdDst.Password,
                TrustServerCertificate = ChkDstTrust.IsChecked == true
            };

        private DbMigrateOptions ReadOptions() => new()
        {
            CreateTargetDatabase = RadDstCreate.IsChecked == true,
            IncludeData = ChkData.IsChecked == true,
            IncludeCode = ChkCode.IsChecked == true,
            IncludeIndexes = ChkIndexes.IsChecked == true,
            VerifyChecksums = ChkChecksum.IsChecked == true,
            DropCreatedDatabaseOnFailure = ChkDropOnFail.IsChecked == true,
            BatchSize = (int)SldBatch.Value,
            Parallelism = (int)SldParallel.Value
        };

        /// <summary>What the plan was built for (passwords left out). If this changes after Analyze, the plan is stale.</summary>
        private string Signature()
        {
            var s = ReadEndpoint(true);
            var t = ReadEndpoint(false);
            var o = ReadOptions();
            return string.Join("|", s.Server, s.Database, s.Auth, s.User, s.TrustServerCertificate,
                t.Server, t.Database, t.Auth, t.User, t.TrustServerCertificate,
                o.CreateTargetDatabase, o.IncludeData, o.IncludeCode, o.IncludeIndexes);
        }

        private void AddLocalLog(DbMigrateSeverity severity, string source, string text) =>
            OnLog(new DbMigrateLogLine { Severity = severity, Source = source, Text = text });

        // =================================================================
        //  Sidebar
        // =================================================================

        private void BtnCollapseOptions_Click(object sender, RoutedEventArgs e)
        {
            _optionsCollapsed = !_optionsCollapsed;
            OptionsColumn.Width = new GridLength(_optionsCollapsed ? SidebarCollapsedWidth : SidebarExpandedWidth);
            var visibility = _optionsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            TxtOptionsHeaderLabel.Visibility = visibility;
            SepHeader.Visibility = visibility;
            OptionsScrollViewer.Visibility = visibility;
            BtnCollapseOptions.HorizontalContentAlignment = _optionsCollapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            if (BtnCollapseOptions.Content is StackPanel header)
                header.Margin = _optionsCollapsed ? new Thickness(0) : new Thickness(16, 0, 0, 0);
        }

        private void AuthMode_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            UpdateAuthPanels();
        }

        private void UpdateAuthPanels()
        {
            PanelSrcSql.Visibility = RadSrcSql.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelDstSql.Visibility = RadDstSql.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TargetMode_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            SaveSettings();
        }

        private void SldBatch_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            TxtBatchValue.Text = ((int)e.NewValue).ToString("N0");
        }

        private void SldParallel_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_viewConstructed) return;
            TxtParallelValue.Text = ((int)e.NewValue).ToString();
        }

        private void SetStatus(TextBlock label, string text, string brushKey)
        {
            label.Text = text;
            label.ToolTip = text;
            label.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string side }) return;
            var isSource = side == "src";
            var status = isSource ? TxtSrcStatus : TxtDstStatus;
            var endpoint = ReadEndpoint(isSource);
            if (string.IsNullOrWhiteSpace(endpoint.Server)) { SetStatus(status, S("Str_DbMigrate_NeedServer"), "StatusWarningFg"); return; }

            // for a target that will be created the database doesn't exist yet: test the server only
            if (!isSource && RadDstCreate.IsChecked == true) endpoint.Database = "";

            SetStatus(status, S("Str_DbMigrate_Connecting"), "SidebarTextMuted");
            try
            {
                var info = await _manager.TestConnectionAsync(endpoint);
                SetStatus(status, S("Str_DbMigrate_Connected", info.Major, info.ProductVersion), "StatusSuccessFg");
            }
            catch (Exception ex)
            {
                SetStatus(status, DbMigrateManager.FirstLine(ex.Message), "StatusErrorFg");
            }
        }

        private async void BtnList_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string side }) return;
            var isSource = side == "src";
            var status = isSource ? TxtSrcStatus : TxtDstStatus;
            var combo = isSource ? CmbSrcDatabase : CmbDstDatabase;
            var endpoint = ReadEndpoint(isSource);
            if (string.IsNullOrWhiteSpace(endpoint.Server)) { SetStatus(status, S("Str_DbMigrate_NeedServer"), "StatusWarningFg"); return; }

            SetStatus(status, S("Str_DbMigrate_Connecting"), "SidebarTextMuted");
            try
            {
                var names = await _manager.ListDatabasesAsync(endpoint);
                var keep = combo.Text;
                combo.ItemsSource = names;
                combo.Text = keep;
                combo.IsDropDownOpen = names.Count > 0;
                SetStatus(status, S("Str_DbMigrate_DatabasesFound", names.Count), "StatusSuccessFg");
            }
            catch (Exception ex)
            {
                SetStatus(status, DbMigrateManager.FirstLine(ex.Message), "StatusErrorFg");
            }
        }

        // =================================================================
        //  Tabs
        // =================================================================

        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewConstructed) return;
            if (sender is RadioButton { Tag: string tag }) ShowTab(tag);
        }

        private void ShowTab(string tag)
        {
            PageTables.Visibility = tag == "tables" ? Visibility.Visible : Visibility.Collapsed;
            PageNotes.Visibility = tag == "notes" ? Visibility.Visible : Visibility.Collapsed;
            PageConsole.Visibility = tag == "console" ? Visibility.Visible : Visibility.Collapsed;
            ConsoleTools.Visibility = tag == "console" ? Visibility.Visible : Visibility.Collapsed;
            var radio = tag switch { "notes" => TabNotes, "console" => TabConsole, _ => TabTables };
            radio.IsChecked = true;
            if (tag == "console") ScrollConsoleToEnd();
        }

        // =================================================================
        //  Analyze
        // =================================================================

        private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _closed) return;
            var source = ReadEndpoint(true);
            var target = ReadEndpoint(false);
            var options = ReadOptions();
            if (string.IsNullOrWhiteSpace(source.Server) || string.IsNullOrWhiteSpace(target.Server))
            {
                MessageBox.Show(S("Str_DbMigrate_NeedServers"), S("Str_DbMigrate_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrWhiteSpace(source.Database))
            {
                MessageBox.Show(S("Str_DbMigrate_NeedSourceDb"), S("Str_DbMigrate_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SaveSettings();
            SetBusy(true);
            _plan = null;
            _lastResult = null;
            BtnMigrate.IsEnabled = false;
            SetReportButtons(false);
            try
            {
                var plan = await _manager.BuildPlanAsync(source, target, options);
                _plan = plan;
                _planSignature = Signature();
                ShowPlan(plan);
            }
            catch (OperationCanceledException)
            {
                AddLocalLog(DbMigrateSeverity.Warning, "PLAN", S("Str_DbMigrate_Cancelled"));
            }
            catch (Exception ex)
            {
                AddLocalLog(DbMigrateSeverity.Blocker, "PLAN", DbMigrateManager.FirstLine(ex.Message));
                ShowTab("console");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ShowPlan(DbMigratePlan plan)
        {
            _tables.Clear();
            foreach (var t in plan.Tables) _tables.Add(t);
            _notes.Clear();
            foreach (var n in plan.Issues.OrderByDescending(i => i.Severity).ThenBy(i => i.Object)) _notes.Add(n);

            var blockers = plan.Issues.Count(i => i.Severity == DbMigrateSeverity.Blocker);
            var warnings = plan.Issues.Count(i => i.Severity == DbMigrateSeverity.Warning);

            TablesGrid.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
            UiMotion.FadeIn(TablesGrid);
            TxtNoNotes.Visibility = _notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtTabTables.Text = $"{S("Str_DbMigrate_TabTables")} ({plan.Tables.Count})";
            TxtTabNotes.Text = $"{S("Str_DbMigrate_TabNotes")} ({_notes.Count})";

            PillsRow.Visibility = Visibility.Visible;
            TxtPillSource.Text = $"{plan.Source.ServerName}  v{plan.Source.ProductVersion}";
            TxtPillTarget.Text = $"{plan.Target.ServerName}  v{plan.Target.ProductVersion}";
            TxtPillDirection.Text = S("Str_DbMigrate_Dir_" + plan.Direction);
            IconPillDirection.Kind = plan.Direction switch
            {
                DbMigrateDirection.Upgrade => PackIconKind.ArrowUpBold,
                DbMigrateDirection.Downgrade => PackIconKind.ArrowDownBold,
                _ => PackIconKind.ArrowRightBold
            };
            TxtPillIssues.Text = S("Str_DbMigrate_PillIssues", blockers, warnings);
            TxtPillIssues.SetResourceReference(TextBlock.ForegroundProperty, blockers > 0 ? "StatusErrorFg" : warnings > 0 ? "StatusWarningFg" : "StatusSuccessFg");
            IconPillIssues.Kind = blockers > 0 ? PackIconKind.CloseOctagonOutline : warnings > 0 ? PackIconKind.AlertOutline : PackIconKind.CheckCircleOutline;
            UiMotion.Pop(TxtPillIssues);

            BtnMigrate.IsEnabled = !plan.HasBlockers;
            BtnSaveScript.IsEnabled = true;
            SetReportButtons(true);
            if (plan.HasBlockers || warnings > 0) ShowTab("notes");
            else ShowTab("tables");
        }

        // =================================================================
        //  Migrate
        // =================================================================

        private async void BtnMigrate_Click(object sender, RoutedEventArgs e)
        {
            if (_closed) return;
            if (_running)
            {
                _manager.CancelActiveRun();
                TxtMigrate.Text = S("Str_DbMigrate_Cancelling");
                BtnMigrate.IsEnabled = false;
                return;
            }
            if (_busy || _plan == null || _plan.HasBlockers) return;

            if (_planSignature != Signature())
            {
                MessageBox.Show(S("Str_DbMigrate_StaleBody"), S("Str_DbMigrate_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var source = ReadEndpoint(true);
            var target = ReadEndpoint(false);
            var options = ReadOptions();
            var plan = _plan;

            var include = plan.Tables.Count(t => t.Include && t.Unsupported == null);
            var left = plan.Tables.Count - include;
            var body = new StringBuilder();
            body.AppendLine(S("Str_DbMigrate_ConfirmFrom", source.Database, plan.Source.ServerName, plan.Source.Major));
            body.AppendLine(S("Str_DbMigrate_ConfirmTo", target.Database, plan.Target.ServerName, plan.Target.Major));
            body.AppendLine();
            body.AppendLine(options.CreateTargetDatabase ? S("Str_DbMigrate_ConfirmCreates") : S("Str_DbMigrate_ConfirmUsesExisting"));
            body.AppendLine(S("Str_DbMigrate_ConfirmTables", include, plan.Tables.Count));
            if (left > 0) body.AppendLine(S("Str_DbMigrate_ConfirmLeftOut", left));
            if (!options.IncludeData) body.AppendLine(S("Str_DbMigrate_ConfirmNoData"));
            var warnings = plan.Issues.Count(i => i.Severity == DbMigrateSeverity.Warning);
            if (warnings > 0) body.AppendLine(S("Str_DbMigrate_ConfirmWarnings", warnings));
            body.AppendLine();
            body.Append(S("Str_DbMigrate_ConfirmSourceSafe"));

            if (MessageBox.Show(body.ToString(), S("Str_DbMigrate_ConfirmTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            SaveSettings();
            foreach (var t in plan.Tables) { t.State = DbMigrateStepState.Pending; t.RowsCopied = 0; if (t.Unsupported == null) t.Message = ""; }

            _running = true;
            _lastProgress = null;
            _runStart = DateTime.Now;
            SetBusy(true);
            SetReportButtons(false);
            IconMigrate.Kind = PackIconKind.StopCircleOutline;
            TxtMigrate.Text = S("Str_DbMigrate_Cancel");
            BtnMigrate.IsEnabled = true;              // the button is "Cancel" while running
            PillFailed.Visibility = Visibility.Collapsed;
            BarOverall.Value = 0;
            ProgressCard.Visibility = Visibility.Visible;
            UiMotion.FadeIn(ProgressCard);
            TxtStage.Text = S("Str_DbMigrate_Stage_Connecting");
            TxtPercent.Text = "0%";
            _clock.Start();
            ShowTab("tables");

            DbMigrateResult result;
            try
            {
                result = await _manager.RunAsync(plan, source, target, options, new Progress<DbMigrateProgress>(OnProgress));
            }
            finally
            {
                _clock.Stop();
                _running = false;
            }

            _lastResult = result;
            UpdateTimePill();
            SetBusy(false);
            SetReportButtons(true);
            IconMigrate.Kind = PackIconKind.DatabaseArrowRightOutline;
            TxtMigrate.Text = S("Str_DbMigrate_Migrate");
            BtnMigrate.IsEnabled = false;             // a finished run needs a new Analyze (the target now exists)
            FinishRun(result, options, target);
        }

        private async void FinishRun(DbMigrateResult result, DbMigrateOptions options, DbMigrateEndpoint target)
        {
            if (_closed) return;
            BarOverall.Value = result.Success ? 1 : BarOverall.Value;
            TxtStage.Text = result.Success ? S("Str_DbMigrate_Stage_Done") : result.Cancelled ? S("Str_DbMigrate_Stage_Cancelled") : S("Str_DbMigrate_Stage_Failed");
            if (result.TablesFailed + result.ObjectsFailed > 0)
            {
                PillFailed.Visibility = Visibility.Visible;
                TxtPillFailed.Text = S("Str_DbMigrate_PillFailed", result.TablesFailed + result.ObjectsFailed);
            }

            if (result.Success)
            {
                MessageBox.Show(S("Str_DbMigrate_DoneBody", result.TablesCopied, result.RowsCopied.ToString("N0"), result.ObjectsCreated, result.Elapsed.ToString(@"hh\:mm\:ss")),
                    S("Str_DbMigrate_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ShowTab(result.Failures.Count > 0 ? "console" : "tables");
            if (result.Cancelled)
                MessageBox.Show(S("Str_DbMigrate_CancelledBody"), S("Str_DbMigrate_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show(S("Str_DbMigrate_ProblemsBody", result.Failures.Count), S("Str_DbMigrate_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);

            // a database this run created and left half-filled: offer to remove it (default No)
            if (result.DatabaseCreated && !result.DatabaseDropped)
            {
                var drop = MessageBox.Show(S("Str_DbMigrate_DropAsk", target.Database), S("Str_DbMigrate_Title"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (drop == MessageBoxResult.Yes) result.DatabaseDropped = await _manager.DropCreatedDatabaseAsync();
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            BtnAnalyze.IsEnabled = !busy;
            OptionsScrollViewer.IsEnabled = !busy;
            if (!busy && !_running) BtnMigrate.IsEnabled = _plan != null && !_plan.HasBlockers && _lastResult == null;
            if (busy && !_running) BtnMigrate.IsEnabled = false;
        }

        private void SetReportButtons(bool enabled)
        {
            BtnCopyReport.IsEnabled = enabled && _plan != null;
            BtnSaveReport.IsEnabled = enabled && _plan != null;
            BtnSaveScript.IsEnabled = enabled && _plan != null;
        }

        // =================================================================
        //  Progress
        // =================================================================

        private void OnProgress(DbMigrateProgress p)
        {
            if (_closed) return;
            _lastProgress = p;
            var dataFraction = p.RowsTotal > 0 ? (double)p.RowsDone / p.RowsTotal : p.TablesTotal > 0 ? (double)(p.TablesDone + p.TablesFailed) / p.TablesTotal : 1;
            var overall = p.Stage switch
            {
                DbMigrateStage.Connecting => 0.0,
                DbMigrateStage.Structure => 0.03,
                DbMigrateStage.Data => 0.05 + 0.80 * Math.Min(1, dataFraction),
                DbMigrateStage.Indexes => 0.87,
                DbMigrateStage.Code => 0.92,
                DbMigrateStage.Finishing => 0.96,
                DbMigrateStage.Verifying => 0.98,
                DbMigrateStage.Done => 1.0,
                _ => BarOverall.Value
            };
            BarOverall.Value = Math.Clamp(overall, 0, 1);
            TxtPercent.Text = $"{BarOverall.Value:P0}";
            TxtStage.Text = string.IsNullOrEmpty(p.Message) ? S("Str_DbMigrate_Stage_" + p.Stage) : $"{S("Str_DbMigrate_Stage_" + p.Stage)} - {p.Message}";
            TxtPillTables.Text = S("Str_DbMigrate_PillTables", p.TablesDone, p.TablesTotal);
            TxtPillRows.Text = S("Str_DbMigrate_PillRows", p.RowsDone.ToString("N0"), p.RowsTotal.ToString("N0"));
            var failed = p.TablesFailed + p.ObjectsFailed;
            PillFailed.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtPillFailed.Text = S("Str_DbMigrate_PillFailed", failed);
            UpdateTimePill();
        }

        private void UpdateTimePill()
        {
            var elapsed = _running ? DateTime.Now - _runStart : _lastResult?.Elapsed ?? TimeSpan.Zero;
            var text = elapsed.ToString(@"hh\:mm\:ss");
            if (_running && _lastProgress is { Stage: DbMigrateStage.Data, RowsDone: > 0 } p && p.RowsTotal > p.RowsDone)
            {
                var remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds * (p.RowsTotal - p.RowsDone) / p.RowsDone);
                text += "  " + S("Str_DbMigrate_PillEta", remaining.ToString(@"hh\:mm\:ss"));
            }
            TxtPillTime.Text = text;
        }

        // =================================================================
        //  Console
        // =================================================================

        private void OnLog(DbMigrateLogLine line)
        {
            if (_closed) return;
            _log.Add(line);
            if (_log.Count > MaxLogLines)
                for (var i = _log.Count - MaxLogLines; i > 0; i--) _log.RemoveAt(0);
            if (PageConsole.Visibility == Visibility.Visible) ScrollConsoleToEnd();
        }

        private void ScrollConsoleToEnd()
        {
            if (_consoleScroll == null) _consoleScroll = FindScrollViewer(ConsoleList);
            if (_consoleScroll != null && !_scrollHooked)
            {
                _scrollHooked = true;
                // follow new lines only while the user is at the bottom
                _consoleScroll.ScrollChanged += (_, e) =>
                {
                    if (e.ExtentHeightChange == 0) _followLog = _consoleScroll.VerticalOffset >= _consoleScroll.ScrollableHeight - 2;
                };
            }
            if (!_followLog || _log.Count == 0) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_log.Count > 0) ConsoleList.ScrollIntoView(_log[^1]);
            }, DispatcherPriority.Background);
        }

        private bool _scrollHooked;

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is ScrollViewer sv) return sv;
                if (FindScrollViewer(child) is { } found) return found;
            }
            return null;
        }

        private string LogText() => string.Join(Environment.NewLine, _log.Select(l => $"{l.TimeText} [{l.Source}] {l.Text}"));

        private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            if (_log.Count == 0) return;
            Clipboard.SetText(LogText());
            Flash(IconCopyLog);
        }

        private void BtnSaveLog_Click(object sender, RoutedEventArgs e)
        {
            if (_log.Count == 0) return;
            var dlg = new SaveFileDialog { Filter = S("Str_DbMigrate_TextFilter"), FileName = "db-migrate-log.txt" };
            if (dlg.ShowDialog() == true) File.WriteAllText(dlg.FileName, LogText(), new UTF8Encoding(true));
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e) { _log.Clear(); _followLog = true; }

        /// <summary>Copy buttons swap to a tick for a moment (the wording lives in the tooltip).</summary>
        private void Flash(PackIcon icon)
        {
            var original = icon.Kind;
            icon.Kind = PackIconKind.Check;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
            timer.Tick += (_, _) => { timer.Stop(); icon.Kind = original; };
            timer.Start();
        }

        // =================================================================
        //  Report and script
        // =================================================================

        private void BtnCopyReport_Click(object sender, RoutedEventArgs e)
        {
            if (_plan == null) return;
            Clipboard.SetText(DbMigrateManager.BuildReport(_plan, ReadEndpoint(true), ReadEndpoint(false), _lastResult));
            Flash(IconCopyReport);
        }

        private void BtnSaveReport_Click(object sender, RoutedEventArgs e)
        {
            if (_plan == null) return;
            var dlg = new SaveFileDialog { Filter = S("Str_DbMigrate_TextFilter"), FileName = "db-migrate-report.txt" };
            if (dlg.ShowDialog() == true)
                File.WriteAllText(dlg.FileName, DbMigrateManager.BuildReport(_plan, ReadEndpoint(true), ReadEndpoint(false), _lastResult), new UTF8Encoding(true));
        }

        private void BtnSaveScript_Click(object sender, RoutedEventArgs e)
        {
            if (_plan == null) return;
            var target = ReadEndpoint(false);
            var name = string.IsNullOrWhiteSpace(target.Database) ? ReadEndpoint(true).Database : target.Database;
            var dlg = new SaveFileDialog { Filter = S("Str_DbMigrate_SqlFilter"), FileName = $"{name}-structure.sql" };
            if (dlg.ShowDialog() != true) return;
            // UTF-8 with a BOM so SQL Server Management Studio reads accented names correctly
            File.WriteAllText(dlg.FileName, _manager.BuildSchemaScript(_plan, name, ReadOptions()), new UTF8Encoding(true));
        }
    }
}
