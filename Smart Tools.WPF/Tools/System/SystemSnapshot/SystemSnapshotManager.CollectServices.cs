using Microsoft.Win32;
using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Management;

namespace Smart_Tools.WPF.Controllers
{
    public sealed partial class SystemSnapshotManager
    {
        // =================================================================
        //  Services, startup items and scheduled tasks: what starts by itself and what it depends on
        // =================================================================

        private List<SnapshotTable> CollectServices(SnapshotOptions options, CancellationToken token)
        {
            var tables = new List<SnapshotTable> { Guard("services", ReadServices) };
            token.ThrowIfCancellationRequested();
            tables.Add(Guard("startup", ReadStartup));
            token.ThrowIfCancellationRequested();
            tables.Add(Guard("tasks", () => ReadScheduledTasks(options, token)));
            return tables;
        }

        /// <summary>The name inside a WMI reference such as \\PC\root\cimv2:Win32_Service.Name="Dhcp".</summary>
        private static string NameInReference(string reference)
        {
            const string marker = "Name=\"";
            int start = reference.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return "";
            start += marker.Length;
            int end = reference.IndexOf('"', start);
            return end < 0 ? "" : reference[start..end];
        }

        private SnapshotTable ReadServices()
        {
            // service -> the services it needs before it can start
            var needs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in Wmi("SELECT Antecedent, Dependent FROM Win32_DependentService"))
            {
                string required = NameInReference(Prop(link, "Antecedent")), dependent = NameInReference(Prop(link, "Dependent"));
                if (required.Length == 0 || dependent.Length == 0) continue;
                if (!needs.TryGetValue(dependent, out var list)) needs[dependent] = list = new List<string>();
                list.Add(required);
            }

            var rows = new ServicesManager().Scan().Select(s => new[]
            {
                s.Name, s.DisplayName,
                s.State.ToString(),
                s.StartMode == WinServiceStartMode.AutomaticDelayed ? "Automatic (Delayed)" : s.StartMode.ToString(),
                s.Account,
                _redactor.Text(s.BinaryPath),
                needs.TryGetValue(s.Name, out var n) ? string.Join(", ", n) : "",
                Text(s.IsCore),
                s.Description,
            }).OrderBy(r => r[1], StringComparer.CurrentCultureIgnoreCase).ToList();

            return Make("services", new[] { "Name", "Display name", "State", "Start type", "Runs as", "Program", "Needs (dependencies)", "Core to Windows", "Description" }, rows,
                Note("\"Needs\" lists the services that must be running first.", RedactionNote()));
        }

        private SnapshotTable ReadStartup()
        {
            var rows = new List<string[]>();
            foreach (var app in new StartupAppsManager().Scan())
            {
                rows.Add(new[]
                {
                    app.SourceName, app.Name, _redactor.Text(app.Command), app.FilePath, app.Publisher,
                    Text(app.IsEnabled), app.FileMissing ? "File missing" : "",
                });
            }

            // Places the Startup Apps tool does not list: run-once keys and the Winlogon values that start the desktop.
            foreach (var (hive, label) in new[] { (Registry.CurrentUser, "Current user (RunOnce)"), (Registry.LocalMachine, "All users (RunOnce)") })
            {
                using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce");
                foreach (string name in key?.GetValueNames() ?? Array.Empty<string>())
                    rows.Add(new[] { label, name, _redactor.Text(RegText(key, name)), "", "", "Yes", "Runs once, then is removed" });
            }
            using (var winlogon = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
            {
                foreach (string name in new[] { "Shell", "Userinit" })
                    rows.Add(new[] { "Windows sign-in", name, RegText(winlogon, name), "", "", "Yes", "" });
            }

            return Make("startup", new[] { "Where", "Name", "Command", "Program", "Publisher", "Enabled", "Note" }, rows, RedactionNote());
        }

        private static string TaskState(string value) => value switch
        {
            "0" => "Unknown", "1" => "Disabled", "2" => "Queued", "3" => "Ready", "4" => "Running", _ => value,
        };

        private SnapshotTable ReadScheduledTasks(SnapshotOptions options, CancellationToken token)
        {
            var rows = new List<string[]>();
            int skipped = 0;
            foreach (var task in Wmi("SELECT * FROM MSFT_ScheduledTask", @"root\Microsoft\Windows\TaskScheduler"))
            {
                token.ThrowIfCancellationRequested();
                string path = Prop(task, "TaskPath");
                if (!options.IncludeMicrosoftTasks && path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }

                var actions = new List<string>();
                if (task["Actions"] is ManagementBaseObject[] list)
                {
                    foreach (var action in list)
                        actions.Add(_redactor.Text($"{Prop(action, "Execute")} {Prop(action, "Arguments")}".Trim()));
                }

                var triggers = new List<string>();
                if (task["Triggers"] is ManagementBaseObject[] triggerList)
                {
                    foreach (var trigger in triggerList)
                        triggers.Add(Prop(trigger, "__CLASS").Replace("MSFT_Task", "").Replace("Trigger", ""));
                }

                string user = "";
                if (task["Principal"] is ManagementBaseObject principal) user = Prop(principal, "UserId");

                rows.Add(new[] { Prop(task, "TaskName"), path, TaskState(Prop(task, "State")), user, string.Join(" | ", actions), string.Join(", ", triggers), Prop(task, "Author") });
            }

            rows.Sort((a, b) => string.Compare(a[1] + a[0], b[1] + b[0], StringComparison.CurrentCultureIgnoreCase));
            return Make("tasks", new[] { "Name", "Folder", "State", "Runs as", "Action", "Starts on", "Author" }, rows,
                Note(skipped > 0 ? $"{skipped} tasks under \\Microsoft\\ were left out (tick \"Include Windows' own scheduled tasks\" to list them)." : null, RedactionNote()));
        }
    }
}
