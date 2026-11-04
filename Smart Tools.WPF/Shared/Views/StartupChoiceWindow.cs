using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The first-run question, like Advanced IP Scanner's: <b>Install</b> or <b>Run</b> (portable). Shown only when
    /// <see cref="AppMode.Detect"/> says Ask, before anything is written. Built in code in the Windows colours because
    /// the app's own theme and language are not loaded yet.
    /// </summary>
    public sealed class StartupChoiceWindow : Window
    {
        public enum Outcome { Cancelled, Run, Installed }

        public Outcome Result { get; private set; } = Outcome.Cancelled;

        private readonly CheckBox _desktop = new();
        private readonly TextBlock _message = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        private readonly Func<IProgress<CopyProgress>, CancellationToken, Task<InstallResult>> _install;

        public StartupChoiceWindow()
        {
            _install = (progress, token) => AppInstaller.InstallAsync(AppMode.ExeFolder, _desktop.IsChecked == true, false, true, progress, token);

            Title = AppMode.ProductName;
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            Background = WindowsPalette.Brush(WindowsPalette.Background);

            var heading = new TextBlock
            {
                Text = WindowsPalette.T("Welcome to Smart Tools", "Bienvenido a Smart Tools"),
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = WindowsPalette.Brush(WindowsPalette.Text),
            };
            var subtitle = new TextBlock
            {
                Text = WindowsPalette.T("How do you want to use it on this PC?", "¿Cómo quieres usarlo en este equipo?"),
                FontSize = 13,
                Margin = new Thickness(0, 4, 0, 18),
                Foreground = WindowsPalette.Brush(WindowsPalette.Muted),
            };

            var install = ChoiceButton("",
                WindowsPalette.T("Install", "Instalar"),
                WindowsPalette.T("Adds a Start menu shortcut and an entry in Apps & features, so you can uninstall it. Your settings are kept on this PC.",
                                 "Agrega un acceso directo en el menú Inicio y una entrada en Aplicaciones, para poder desinstalarlo. Tus ajustes se guardan en este equipo."));
            install.Click += (_, _) => StartInstall();

            var run = ChoiceButton("",
                WindowsPalette.T("Run", "Ejecutar"),
                WindowsPalette.T("Portable: nothing is installed. Your settings stay in a UserData folder next to the app, so you can carry it on a USB drive.",
                                 "Portátil: no se instala nada. Tus ajustes quedan en una carpeta UserData junto a la aplicación, para llevarla en una memoria USB."));
            run.Margin = new Thickness(0, 10, 0, 0);
            run.Click += (_, _) => ChooseRun();

            _desktop.Content = WindowsPalette.T("Add a shortcut on the desktop (when installing)", "Agregar un acceso directo en el escritorio (al instalar)");
            _desktop.Margin = new Thickness(2, 16, 0, 0);
            _desktop.IsChecked = true;
            _desktop.Foreground = WindowsPalette.Brush(WindowsPalette.Text);

            var where = new TextBlock
            {
                Text = WindowsPalette.T("Installs to: ", "Se instala en: ") + AppMode.DefaultInstallFolder + WindowsPalette.T("  (no administrator needed)", "  (no requiere administrador)"),
                FontSize = 11,
                Margin = new Thickness(2, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = WindowsPalette.Brush(WindowsPalette.Muted),
            };

            var panel = new StackPanel { Margin = new Thickness(28, 26, 28, 24) };
            panel.Children.Add(heading);
            panel.Children.Add(subtitle);
            panel.Children.Add(install);
            panel.Children.Add(run);
            panel.Children.Add(_desktop);
            panel.Children.Add(where);
            panel.Children.Add(_message);
            Content = panel;
        }

        /// <summary>A big two-line button with an icon on the left, drawn with a template so hover and focus show clearly.</summary>
        private static Button ChoiceButton(string glyph, string title, string description)
        {
            var template = (ControlTemplate)XamlReader.Parse(
                $@"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
                     <Border x:Name='B' Background='{WindowsPalette.Card}' BorderBrush='{WindowsPalette.Line}' BorderThickness='1' CornerRadius='12' Padding='18,14'>
                       <ContentPresenter />
                     </Border>
                     <ControlTemplate.Triggers>
                       <Trigger Property='IsMouseOver' Value='True'>
                         <Setter TargetName='B' Property='BorderBrush' Value='{WindowsPalette.Accent}' />
                         <Setter TargetName='B' Property='Background' Value='{WindowsPalette.CardHover}' />
                       </Trigger>
                       <Trigger Property='IsKeyboardFocused' Value='True'>
                         <Setter TargetName='B' Property='BorderBrush' Value='{WindowsPalette.Accent}' />
                       </Trigger>
                     </ControlTemplate.Triggers>
                   </ControlTemplate>");

            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 28,
                Foreground = WindowsPalette.Brush(WindowsPalette.Accent),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 18, 0),
            };
            var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = WindowsPalette.Brush(WindowsPalette.Text) };
            var body = new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), Foreground = WindowsPalette.Brush(WindowsPalette.Muted) };
            var text = new StackPanel();
            text.Children.Add(heading);
            text.Children.Add(body);

            var row = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(text);

            return new Button { Template = template, Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, Cursor = System.Windows.Input.Cursors.Hand };
        }

        private void ShowMessage(string text, bool error)
        {
            _message.Text = text;
            _message.Foreground = WindowsPalette.Brush(error ? WindowsPalette.Error : WindowsPalette.Muted);
            _message.Visibility = Visibility.Visible;
        }

        private void ChooseRun()
        {
            if (!AppInstaller.TryMakePortableHere())
            {
                ShowMessage(WindowsPalette.T(
                    "This folder is read-only, so the app cannot keep its settings next to itself. Copy the app to a folder you can write to (or a USB drive), or choose Install.",
                    "Esta carpeta es de solo lectura, así que la aplicación no puede guardar sus ajustes junto a sí misma. Copia la aplicación a una carpeta donde puedas escribir (o a una memoria USB), o elige Instalar."), error: true);
                return;
            }
            AppMode.SetPortable();
            Result = Outcome.Run;
            Close();
        }

        private void StartInstall()
        {
            _message.Visibility = Visibility.Collapsed;
            IsEnabled = false;
            var outcome = CopyProgressWindow.Run(this, WindowsPalette.T("Installing Smart Tools...", "Instalando Smart Tools..."), _install);
            IsEnabled = true;

            if (outcome.Ok && outcome.ExePath != null)
            {
                AppInstaller.Launch(outcome.ExePath);
                Result = Outcome.Installed;
                Close();
            }
            else if (!outcome.Cancelled)
            {
                ShowMessage(WindowsPalette.T("Could not install: ", "No se pudo instalar: ") + outcome.Message, error: true);
            }
        }
    }
}
