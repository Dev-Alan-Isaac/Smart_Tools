using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using Smart_Tools.WPF;
using Smart_Tools.WPF.Controllers;
using Smart_Tools.WPF.Models;

namespace SystemSnapshotCollector
{
    /// <summary>
    /// The whole collector: tick what to read, press Scan, and the HTML report is saved next to the program and opened.
    /// The scan and the report are the System Snapshot tool's own (SystemSnapshotManager); this file is only the window.
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly SystemSnapshotManager _manager = new SystemSnapshotManager();
        private readonly Dictionary<string, CheckBox> _groupBoxes = new Dictionary<string, CheckBox>();

        private readonly Label _title = new Label();
        private readonly Label _intro = new Label();
        private readonly ComboBox _language = new ComboBox();
        private readonly GroupBox _groupsBox = new GroupBox();
        private readonly GroupBox _optionsBox = new GroupBox();
        private readonly Button _all = new Button();
        private readonly Button _none = new Button();
        private readonly CheckBox _msTasks = new CheckBox();
        private readonly CheckBox _builtInRules = new CheckBox();
        private readonly Label _adminNote = new Label();
        private readonly Button _scan = new Button();
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly Label _status = new Label();
        private readonly Button _open = new Button();
        private readonly Button _folder = new Button();

        private CancellationTokenSource? _cts;
        private string? _savedPath;
        private bool _scanning;

        public MainForm()
        {
            Font = new Font("Segoe UI", 9F);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            ClientSize = new Size(560, 640);
            Padding = new Padding(16);

            _title.Font = new Font(Font.FontFamily, 14F, FontStyle.Bold);
            _title.SetBounds(16, 14, 380, 28);
            _intro.SetBounds(16, 46, 528, 38);

            _language.DropDownStyle = ComboBoxStyle.DropDownList;
            _language.Items.AddRange(new object[] { "English", "Español" });
            _language.SelectedIndex = Loc.Language == "es" ? 1 : 0;
            _language.SetBounds(444, 16, 100, 24);
            _language.SelectedIndexChanged += (s, e) => { Loc.Use(_language.SelectedIndex == 1 ? "es" : "en"); BuildGroupTexts(); ApplyTexts(); };

            _groupsBox.SetBounds(16, 92, 528, 262);
            int y = 24;
            foreach (var group in SystemSnapshotManager.Groups)
            {
                var box = new CheckBox { Tag = group.Id, Checked = group.OnByDefault, AutoSize = false, Bounds = new Rectangle(16, y, 490, 24) };
                _groupBoxes[group.Id] = box;
                _groupsBox.Controls.Add(box);
                y += 28;
            }
            _all.SetBounds(16, y + 6, 110, 28);
            _none.SetBounds(132, y + 6, 110, 28);
            _all.Click += (s, e) => SetAll(true);
            _none.Click += (s, e) => SetAll(false);
            _groupsBox.Controls.Add(_all);
            _groupsBox.Controls.Add(_none);

            _optionsBox.SetBounds(16, 362, 528, 96);
            _msTasks.SetBounds(16, 24, 490, 24);
            _builtInRules.SetBounds(16, 54, 490, 24);
            _optionsBox.Controls.Add(_msTasks);
            _optionsBox.Controls.Add(_builtInRules);

            bool admin = SystemSnapshotManager.IsAdministrator();
            _adminNote.SetBounds(16, 466, 528, 34);
            _adminNote.ForeColor = admin ? Color.SeaGreen : Color.DarkOrange;

            _scan.SetBounds(16, 508, 528, 40);
            _scan.Font = new Font(Font.FontFamily, 10.5F, FontStyle.Bold);
            _scan.Click += (s, e) => Scan_Click();

            _bar.SetBounds(16, 556, 528, 8);
            _bar.Maximum = 1000;
            _bar.Visible = false;
            _status.SetBounds(16, 570, 528, 20);

            _open.SetBounds(16, 596, 200, 30);
            _folder.SetBounds(222, 596, 200, 30);
            _open.Enabled = _folder.Enabled = false;
            _open.Click += (s, e) => Launch(_savedPath);
            _folder.Click += (s, e) => { if (_savedPath != null) Launch("/select,\"" + _savedPath + "\"", "explorer.exe"); };

            Controls.AddRange(new Control[] { _title, _intro, _language, _groupsBox, _optionsBox, _adminNote, _scan, _bar, _status, _open, _folder });
            AcceptButton = _scan;
            BuildGroupTexts();
            ApplyTexts();
            FormClosing += (s, e) => _cts?.Cancel();
        }

        private static string T(string key, string english) => Loc.T("Str_SysSnap_" + key, english);

        private void BuildGroupTexts()
        {
            foreach (var group in SystemSnapshotManager.Groups)
                _groupBoxes[group.Id].Text = Loc.T("Str_SysSnap_Group_" + group.Id, group.Title);
        }

        private void ApplyTexts()
        {
            Text = T("C_Title", "System Snapshot Collector");
            _title.Text = Text;
            _intro.Text = T("C_Intro", "Reads this PC and saves a report as a web page (HTML) next to this program. Nothing is changed and nothing is sent anywhere.");
            _groupsBox.Text = T("ScanGroups", "What to scan");
            _optionsBox.Text = T("Options", "Options");
            _all.Text = T("SelectAll", "All");
            _none.Text = T("SelectNone", "None");
            _msTasks.Text = T("MsTasks", "Include Windows' own scheduled tasks");
            _builtInRules.Text = T("BuiltInRules", "Include Windows' own firewall rules");
            bool admin = SystemSnapshotManager.IsAdministrator();
            _adminNote.Text = admin ? T("AdminTip", "Running as administrator: everything can be read.") : T("LimitedTip", "Not running as administrator.");
            _scan.Text = _scanning ? Loc.T("Str_Common_Cancel", "Cancel") : T("Scan", "Scan this PC");
            _open.Text = T("C_Open", "Open report");
            _folder.Text = T("C_Folder", "Show in folder");
        }

        private void SetAll(bool on)
        {
            foreach (var box in _groupBoxes.Values) box.Checked = on;
        }

        private void SetScanning(bool scanning)
        {
            _scanning = scanning;
            _scan.Text = scanning ? Loc.T("Str_Common_Cancel", "Cancel") : T("Scan", "Scan this PC");
            _bar.Visible = scanning;
            _bar.Value = 0;
            _groupsBox.Enabled = _optionsBox.Enabled = _language.Enabled = !scanning;
            if (scanning) _open.Enabled = _folder.Enabled = false;
        }

        private async void Scan_Click()
        {
            if (_scanning)
            {
                _cts?.Cancel();
                return;
            }

            var groups = new HashSet<string>(_groupBoxes.Where(p => p.Value.Checked).Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
            if (groups.Count == 0)
            {
                _status.Text = T("NothingChosen", "Tick at least one group to scan.");
                return;
            }

            var options = new SnapshotOptions { Groups = groups, IncludeMicrosoftTasks = _msTasks.Checked, IncludeBuiltInFirewallRules = _builtInRules.Checked };
            SetScanning(true);
            _cts = new CancellationTokenSource();
            var progress = new Progress<SnapshotProgress>(p =>
            {
                _bar.Value = p.Total == 0 ? 0 : (int)(1000.0 * p.Done / p.Total);
                string title = Loc.T("Str_SysSnap_Group_" + GroupIdOf(p.Title), p.Title);
                _status.Text = Loc.F("Str_SysSnap_Scanning", "Reading {0}... ({1} of {2} done)", title, p.Done, p.Total);
            });

            SystemSnapshot? snapshot = null;
            try
            {
                snapshot = await _manager.ScanAsync(options, progress, _cts.Token);
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                _status.Text = Loc.F("Str_SysSnap_ScanFailed", "The scan failed: {0}", ex.Message);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                SetScanning(false);
            }
            if (snapshot == null) return;

            try
            {
                _savedPath = SaveReport(snapshot);
                int rows = snapshot.Tables.Sum(t => t.Rows.Count);
                _status.Text = Loc.F(snapshot.Complete ? "Str_SysSnap_Done" : "Str_SysSnap_Cancelled", "Done: {0} sections, {1} rows.", snapshot.Tables.Count, rows)
                    + " " + Loc.F("Str_SysSnap_Saved", "Saved {0}", Path.GetFileName(_savedPath));
                _open.Enabled = _folder.Enabled = true;
                Launch(_savedPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _status.Text = Loc.F("Str_SysSnap_SaveFailed", "Couldn't save: {0}", ex.Message);
            }
        }

        /// <summary>The /auto mode: the default groups, no window, report saved but not opened. Returns 0 when saved.</summary>
        internal static int RunUnattended()
        {
            var options = new SnapshotOptions { Groups = new HashSet<string>(SystemSnapshotManager.Groups.Where(g => g.OnByDefault).Select(g => g.Id), StringComparer.OrdinalIgnoreCase) };
            var snapshot = new SystemSnapshotManager().ScanAsync(options, null, CancellationToken.None).GetAwaiter().GetResult();
            SaveReport(snapshot);
            return 0;
        }

        private static string GroupIdOf(string title) => SystemSnapshotManager.Groups.FirstOrDefault(g => g.Title == title)?.Id ?? "";

        /// <summary>Writes the report next to the program (or on the Desktop when that folder is read-only). Never overwrites a file.</summary>
        private static string SaveReport(SystemSnapshot snapshot)
        {
            string html = SystemSnapshotManager.ToHtml(snapshot);
            string name = SystemSnapshotManager.SuggestFileName(snapshot.MachineName, ".html");
            foreach (string folder in new[] { AppDomain.CurrentDomain.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Path.GetTempPath() })
            {
                try
                {
                    string path = UniquePath(Path.Combine(folder, name));
                    string temp = path + ".tmp";
                    File.WriteAllText(temp, html, new System.Text.UTF8Encoding(true));
                    File.Move(temp, path);
                    return path;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // read-only folder (a USB stick with the switch on, Program Files): try the next place
                }
            }
            throw new IOException("No folder could be written to.");
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            string folder = Path.GetDirectoryName(path) ?? "", stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int n = 2; ; n++)
            {
                string candidate = Path.Combine(folder, stem + " (" + n.ToString(CultureInfo.InvariantCulture) + ")" + ext);
                if (!File.Exists(candidate)) return candidate;
            }
        }

        private static void Launch(string? target, string? program = null)
        {
            if (target == null) return;
            try
            {
                Process.Start(program == null ? new ProcessStartInfo(target) { UseShellExecute = true } : new ProcessStartInfo(program, target));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
            {
                // no browser registered for .html: the file is still saved
            }
        }
    }
}
