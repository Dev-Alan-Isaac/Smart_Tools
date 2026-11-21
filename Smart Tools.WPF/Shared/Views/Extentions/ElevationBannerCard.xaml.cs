using Smart_Tools.WPF.Views;
using Smart_Tools.WPF.Controllers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Controls
{
    /// <summary>
    /// "Needs administrator rights" banner shared by tools whose data is
    /// partly hidden without elevation. Stays collapsed when already elevated
    /// or after the user hides it; confirms before relaunching through UAC.
    /// </summary>
    public partial class ElevationBannerCard : UserControl
    {
        private string _title = string.Empty;
        private string _message = string.Empty;

        public ElevationBannerCard()
        {
            InitializeComponent();
            Visibility = Visibility.Collapsed;
        }

        /// <summary>Resource key of the headline.</summary>
        public string TitleKey
        {
            get => _title;
            set
            {
                _title = value;
                TxtTitle.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, value);
            }
        }

        /// <summary>Resource key of the explanation (what is missing without admin rights).</summary>
        public string MessageKey
        {
            get => _message;
            set
            {
                _message = value;
                TxtMessage.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, value);
            }
        }

        /// <summary>Shows the banner unless the app already runs as administrator.</summary>
        public void ShowIfNotElevated()
        {
            if (HardwareSensorsManager.IsAdministrator()) return;
            UiMotion.FadeVisibility(this, true);
        }

        private void BtnDismiss_Click(object sender, RoutedEventArgs e) => UiMotion.FadeVisibility(this, false);

        private void BtnRestart_Click(object sender, RoutedEventArgs e)
        {
            string title = Window.GetWindow(this)?.Title ?? "Smart Tools";
            string question = TryFindResource("Str_HardwareSensors_RestartConfirm") as string ?? "Restart Smart Tools as administrator?";
            if (AppModal.Show(question, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

            // Unsaved work first (while cancelling is still possible), then switch: the elevated
            // copy waits for this one to close, which happens without asking again.
            if (Window.GetWindow(this) is MainWindow main && !main.PrepareForRestart()) return;

            switch (AppRestartManager.StartElevatedCopy(out string? error))
            {
                case AppRestartResult.Started:
                    System.Windows.Application.Current.Shutdown();
                    break;
                case AppRestartResult.Failed:
                    AppModal.Show($"Couldn't restart as administrator: {error}", title, MessageBoxButton.OK, MessageBoxImage.Error);
                    break;
                // Declined: UAC said no - stay as we are.
            }
        }
    }
}
