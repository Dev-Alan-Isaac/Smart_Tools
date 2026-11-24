namespace Smart_Tools.WPF.Views
{
    /// <summary>
    /// Whether any in-window modal (a message box or a <see cref="AppModal.Present"/> card) is open.
    /// Things drawn in the adorner layer - which sits above all of a window's content, the modals included - listen here and
    /// hide while it is on, or they paint across the dimmed page and the modal card (the data grids' rounded outline did).
    /// </summary>
    public static class ModalState
    {
        private static int _open;

        public static bool AnyOpen => _open > 0;

        /// <summary>Raised after every Opened / Closed, on the UI thread.</summary>
        public static event Action? Changed;

        public static void Opened()
        {
            _open++;
            Changed?.Invoke();
        }

        public static void Closed()
        {
            if (_open > 0) _open--;
            Changed?.Invoke();
        }

        /// <summary>For tests: back to "nothing open".</summary>
        public static void Reset() => _open = 0;
    }
}
