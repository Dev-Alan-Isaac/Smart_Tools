using Smart_Tools.WPF.Models;
using System.Windows;

namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Trim one video in a large dialog - UI only. The editor writes <see cref="VcFileItem.TrimStart"/> / <see cref="VcFileItem.TrimEnd"/>
    /// as you go; Done keeps them, Cancel (or Esc, or closing the window) restores the values the item had when this opened.
    /// </summary>
    public partial class VideoCompressorTrimWindow : Window
    {
        private readonly VcFileItem _item;
        private readonly double? _startAtOpen, _endAtOpen;
        private bool _done;

        /// <summary>The window's size for a work area: 85% of it, kept between a size that still fits the editor and a sensible maximum.</summary>
        public static (double Width, double Height) SizeFor(double workWidth, double workHeight) => (
            Math.Min(workWidth, Math.Clamp(workWidth * 0.85, 760, 1280)),
            Math.Min(workHeight, Math.Clamp(workHeight * 0.85, 560, 820)));

        public VideoCompressorTrimWindow(VcFileItem item, Window? owner)
        {
            InitializeComponent();
            _item = item;
            _startAtOpen = item.TrimStart;
            _endAtOpen = item.TrimEnd;

            Owner = owner;
            Title = (TryFindResource("Str_Vc_TrimTitle") as string ?? "Trim") + " · " + item.Name;
            MinWidth = 640;
            MinHeight = 480;

            var work = SystemParameters.WorkArea;
            (Width, Height) = SizeFor(work.Width, work.Height);
            if (owner == null) WindowStartupLocation = WindowStartupLocation.CenterScreen;

            Editor.Item = item;
        }

        /// <summary>Raised on every change, so the host can refresh its estimate while the dialog is open.</summary>
        public event Action? TrimChanged
        {
            add => Editor.TrimChanged += value;
            remove => Editor.TrimChanged -= value;
        }

        private void BtnDone_Click(object sender, RoutedEventArgs e)
        {
            _done = true;
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (_done) return;
            _item.TrimStart = _startAtOpen;
            _item.TrimEnd = _endAtOpen;
        }
    }
}
