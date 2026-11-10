using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SystemSnapshotLauncher
{
    internal static class Program
    {
        // "Release" value of HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full: 378389 = .NET Framework 4.5.
        private const int Net45 = 378389;
        private const string InstallerName = "NDP452-KB2901907-x86-x64-AllOS-ENU.exe";

        [STAThread]
        private static int Main()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string app = Path.Combine(Path.Combine(baseDir, "app"), "SystemSnapshot.exe");
            string installer = Path.Combine(Path.Combine(baseDir, "prerequisites"), InstallerName);
            bool es = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";
            string title = "System Snapshot Collector";

            if (!File.Exists(app))
            {
                MessageBox.Show(es ? "Falta la carpeta \"app\" junto a este programa. Copia la carpeta completa." : "The \"app\" folder is missing next to this program. Copy the whole folder.",
                    title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }

            if (!HasNet45())
            {
                if (!File.Exists(installer))
                {
                    MessageBox.Show(es ? "Este equipo necesita .NET Framework 4.5 o superior y el instalador no esta en la carpeta \"prerequisites\"."
                                       : ".NET Framework 4.5 or newer is needed on this PC and its installer is not in the \"prerequisites\" folder.",
                        title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 3;
                }

                string ask = es
                    ? "Este equipo no tiene .NET Framework 4.5, que el programa necesita.\n\nSe puede instalar ahora desde esta carpeta (sin internet, unos minutos, pide permiso de administrador). Instalar?"
                    : "This PC does not have .NET Framework 4.5, which the program needs.\n\nIt can be installed now from this folder (no internet needed, a few minutes, asks for administrator permission). Install it?";
                if (MessageBox.Show(ask, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;

                int code;
                try
                {
                    var psi = new ProcessStartInfo(installer, "/passive /norestart") { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(installer) };
                    using (Process p = Process.Start(psi)) { p.WaitForExit(); code = p.ExitCode; }
                }
                catch (Win32Exception) { return 1; }   // the administrator prompt was declined

                if (code == 3010 || code == 1641)
                {
                    MessageBox.Show(es ? ".NET Framework se instalo, pero Windows debe reiniciarse. Reinicia el equipo y abre este programa otra vez."
                                       : ".NET Framework was installed, but Windows must restart first. Restart the PC and open this program again.",
                        title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                if (code != 0 || !HasNet45())
                {
                    MessageBox.Show((es ? "No se pudo instalar .NET Framework 4.5 (codigo " : "Could not install .NET Framework 4.5 (code ") + code + ").",
                        title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 4;
                }
            }

            try
            {
                Process.Start(new ProcessStartInfo(app) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app) });
                return 0;
            }
            catch (Win32Exception) { return 1; }   // the administrator prompt was declined
        }

        private static bool HasNet45()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    object release = key == null ? null : key.GetValue("Release");
                    return release is int && (int)release >= Net45;
                }
            }
            catch (Exception) { return false; }
        }
    }
}
