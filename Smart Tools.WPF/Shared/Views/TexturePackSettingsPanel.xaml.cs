using Smart_Tools.WPF.Controllers;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// The Minecraft versions section of Settings - UI only. The version list, the wiki address, the check against the launcher
    /// and the update from the wiki are <see cref="TexturePackVersions"/>; the Texture Pack tool reads the same file.
    /// </summary>
    public partial class TexturePackSettingsPanel : UserControl
    {
        /// <summary>A setting was saved; the host shows its "Saved" pill.</summary>
        public event Action? SettingChanged;

        private bool _loading;
        private bool _busy;

        public TexturePackSettingsPanel()
        {
            InitializeComponent();
        }

        /// <summary>Shows the address in use. Called when the section opens, since the file may have changed meanwhile.</summary>
        public void Load()
        {
            _loading = true;
            try
            {
                TxtWikiUrl.Text = TexturePackVersions.GetWikiUrl();
                TxtWikiUrl.Tag = TexturePackVersions.WikiTemplateUrl;   // shown greyed out if the box is emptied
                TxtUrlError.Visibility = Visibility.Collapsed;
            }
            finally
            {
                _loading = false;
            }
            BtnDefaultUrl.IsEnabled = !string.Equals(TxtWikiUrl.Text.Trim(), TexturePackVersions.WikiTemplateUrl, StringComparison.OrdinalIgnoreCase);
        }

        private void TxtWikiUrl_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            string? problem = TexturePackVersions.ValidateWikiUrl(TxtWikiUrl.Text);
            TxtUrlError.Text = problem ?? "";
            TxtUrlError.Visibility = problem != null ? Visibility.Visible : Visibility.Collapsed;
            BtnSaveUrl.IsEnabled = problem == null;
        }

        private void BtnSaveUrl_Click(object sender, RoutedEventArgs e)
        {
            string? problem = TexturePackVersions.SetWikiUrl(TxtWikiUrl.Text);
            if (problem != null)
            {
                TxtUrlError.Text = problem;
                TxtUrlError.Visibility = Visibility.Visible;
                return;
            }
            Load();
            SetResult(Loc.T("Str_Settings_TpWikiSaved", "Address saved. \"Update from wiki\" will read it from now on."), "StatusSuccessFg");
            SettingChanged?.Invoke();
        }

        private void BtnDefaultUrl_Click(object sender, RoutedEventArgs e)
        {
            TexturePackVersions.SetWikiUrl("");
            Load();
            SetResult(Loc.T("Str_Settings_TpWikiReset", "The built-in address is back."), "StatusSuccessFg");
            SettingChanged?.Invoke();
        }

        /// <summary>Compares the list with the releases your Minecraft launcher knows (works offline).</summary>
        private async void BtnCheck_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true);
            try
            {
                var (ids, error) = await TexturePackVersions.GetReleaseIdsAsync();
                if (error != null) { SetResult(error, "StatusErrorFg"); return; }

                var missing = TexturePackVersions.MissingReleases(ids, TexturePackVersions.Load());
                if (missing.Count == 0)
                {
                    string newest = ids.OrderByDescending(TexturePackVersions.KeyOf, TexturePackVersions.VersionKeyComparer.Instance).First();
                    SetResult(Loc.F("Str_TexturePack_MsgListCovers", "Your list covers every Minecraft release (the newest is {0}).", newest), "StatusSuccessFg");
                    return;
                }
                SetResult(Loc.N(missing.Count, "Str_TexturePack_MsgMissingOne", "Minecraft {0} isn't in your list yet. Press \"Update from wiki\" to fetch the numbers, or \"Add version\" to type one in.",
                    "Str_TexturePack_MsgMissingMany", "Minecraft {0} aren't in your list yet. Press \"Update from wiki\" to fetch the numbers, or \"Add version\" to type one in.",
                    string.Join(", ", missing.Take(5))), "StatusWarningFg");
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>Best effort: reads the wiki table at the saved address and adds or corrects rows in your own file.</summary>
        private async void BtnWiki_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            SetBusy(true);
            try
            {
                var (added, changed, error) = await TexturePackVersions.UpdateFromWikiAsync();
                if (error != null) { SetResult(error, "StatusErrorFg"); return; }
                SetResult(added + changed == 0
                    ? Loc.T("Str_TexturePack_MsgWikiSame", "Your list already matches the Minecraft Wiki.")
                    : Loc.F("Str_TexturePack_MsgWikiUpdated", "Updated from the Minecraft Wiki: {0} added, {1} corrected.", added, changed), "StatusSuccessFg");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            BtnCheck.IsEnabled = BtnWiki.IsEnabled = !busy;
        }

        private void SetResult(string text, string brushKey)
        {
            TxtResult.Text = text;
            TxtResult.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            TxtResult.Visibility = Visibility.Visible;
        }
    }
}
