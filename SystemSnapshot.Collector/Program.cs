using System.Globalization;
using System.Windows.Forms;

namespace SystemSnapshotCollector
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Smart_Tools.WPF.Loc.Use(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "es" : "en");
            // /auto: no window, scan the default groups and save the report (for scripts and for testing).
            if (args.Any(a => a.Equals("/auto", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.ExitCode = MainForm.RunUnattended();
                return;
            }
            Application.Run(new MainForm());
        }
    }
}
