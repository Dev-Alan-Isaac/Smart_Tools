using System.ComponentModel;

namespace Smart_Tools.WPF.Models
{
    /// <summary>Where a startup entry is registered. Entries for all users need administrator rights to change.</summary>
    public enum StartupLocation { UserRegistry, MachineRegistry, MachineRegistry32, UserFolder, CommonFolder }

    public enum StartupChangeResult { Done, NeedsAdministrator, Failed }

    /// <summary>One program that starts with Windows (a Run key value or a shortcut in a Startup folder).</summary>
    public sealed class StartupApp : INotifyPropertyChanged
    {
        private bool _isEnabled;

        public event PropertyChangedEventHandler? PropertyChanged;

        public StartupLocation Location { get; init; }

        /// <summary>The Run key value name, or the file name in the Startup folder. Also the name Windows keeps the on/off flag under.</summary>
        public string SourceName { get; init; } = "";

        public string Name { get; init; } = "";

        /// <summary>The command line (registry) or the shortcut path (folder).</summary>
        public string Command { get; init; } = "";

        /// <summary>The program file the entry starts, when it could be worked out.</summary>
        public string FilePath { get; init; } = "";

        public string Publisher { get; init; } = "";

        public bool FileMissing { get; init; }

        /// <summary>Display text for <see cref="Location"/>; filled in by the view (it knows the language).</summary>
        public string SourceText { get; set; } = "";

        public bool AllUsers => Location is StartupLocation.MachineRegistry or StartupLocation.MachineRegistry32 or StartupLocation.CommonFolder;

        public bool IsFolderItem => Location is StartupLocation.UserFolder or StartupLocation.CommonFolder;

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            }
        }
    }
}
