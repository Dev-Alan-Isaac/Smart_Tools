using System.Drawing;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// The notification-area icon shown while the window is hidden ("close to tray"), so global shortcuts
    /// keep working and the app can be brought back or exited. Created on the UI thread; events come back there.
    /// </summary>
    public sealed class TrayIconManager : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _show;
        private readonly ToolStripMenuItem _exit;

        public event Action? ShowRequested;
        public event Action? ExitRequested;

        public TrayIconManager()
        {
            _show = new ToolStripMenuItem(Str("Str_Tray_Show", "Show Smart Tools"));
            _exit = new ToolStripMenuItem(Str("Str_Tray_Exit", "Exit"));
            _show.Click += (_, _) => ShowRequested?.Invoke();
            _exit.Click += (_, _) => ExitRequested?.Invoke();

            var menu = new ContextMenuStrip();
            menu.Items.Add(_show);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_exit);

            _icon = new NotifyIcon
            {
                Text = "Smart Tools",
                Icon = LoadIcon(),
                ContextMenuStrip = menu,
                Visible = false,
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowRequested?.Invoke();
            };
        }

        /// <summary>The icon's tooltip (Windows keeps only 63 characters).</summary>
        public void SetTooltip(string text)
        {
            if (text.Length > 63) text = text[..60] + "...";
            _icon.Text = text;
        }

        public bool Visible
        {
            get => _icon.Visible;
            set
            {
                if (value)
                {
                    // The language may have changed since the icon was made.
                    _show.Text = Str("Str_Tray_Show", "Show Smart Tools");
                    _exit.Text = Str("Str_Tray_Exit", "Exit");
                }
                _icon.Visible = value;
            }
        }

        private static string Str(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;

        private static Icon LoadIcon()
        {
            try
            {
                if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } icon) return icon;
            }
            catch (Exception ex)
            {
                Log.Info($"Tray icon: {ex.Message}");
            }
            return SystemIcons.Application;
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
    }
}
