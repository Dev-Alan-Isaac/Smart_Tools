// Root namespace on purpose (like Log and Loc): any view can call FileDrop.Attach with no extra using.
namespace Smart_Tools.WPF
{
    /// <summary>Lets a view accept one dropped file. Only reads the path; the view decides what to do with it.</summary>
    public static class FileDrop
    {
        /// <summary>Makes <paramref name="target"/> accept a dropped file and calls <paramref name="onFile"/> with its path (the first one if several are dropped).</summary>
        public static void Attach(System.Windows.UIElement target, System.Action<string> onFile)
        {
            target.AllowDrop = true;
            target.PreviewDragOver += (_, e) =>
            {
                bool files = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop);
                e.Effects = files ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
                e.Handled = files;
            };
            target.PreviewDrop += (_, e) =>
            {
                if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
                e.Handled = true;
                if (System.IO.File.Exists(paths[0])) onFile(paths[0]);
            };
        }
    }
}
