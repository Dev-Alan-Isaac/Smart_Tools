using System.ComponentModel;

namespace Smart_Tools.WPF.Models
{
    public enum HostsRowKind { Blank, Comment, Entry }

    public enum HostsProblem { None, BadAddress, NoNames, BadName }

    public enum HostsSaveResult { Saved, NeedsAdministrator, HasProblems, Failed }

    /// <summary>
    /// One line of the hosts file. Blank and comment lines are kept as they are so saving never rewrites
    /// what the user didn't touch; an entry (an address and its names, on or off) is rebuilt once it changed.
    /// </summary>
    public sealed class HostsRow : INotifyPropertyChanged
    {
        private bool _isEnabled = true;
        private string _address = "", _names = "", _comment = "", _problemText = "";
        private HostsProblem _problem;

        public event PropertyChangedEventHandler? PropertyChanged;

        public HostsRowKind Kind { get; init; }

        /// <summary>The line as read from the file. Written back unchanged unless <see cref="Modified"/>.</summary>
        public string Raw { get; set; } = "";

        public bool Modified { get; set; }

        /// <summary>False = the line is commented out with "# ".</summary>
        public bool IsEnabled { get => _isEnabled; set => Edit(ref _isEnabled, value); }

        public string Address { get => _address; set => Edit(ref _address, value.Trim()); }

        /// <summary>Host names separated by single spaces.</summary>
        public string Names { get => _names; set => Edit(ref _names, string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))); }

        public string Comment { get => _comment; set => Edit(ref _comment, value.Trim()); }

        public HostsProblem Problem
        {
            get => _problem;
            set
            {
                if (_problem == value) return;
                _problem = value;
                Raise(nameof(Problem));
                Raise(nameof(HasProblem));
            }
        }

        public bool HasProblem => _problem != HostsProblem.None;

        /// <summary>Display text for <see cref="Problem"/>; filled in by the view (it knows the language).</summary>
        public string ProblemText
        {
            get => _problemText;
            set
            {
                if (_problemText == value) return;
                _problemText = value;
                Raise(nameof(ProblemText));
            }
        }

        private void Edit<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            Modified = true;
            Raise(name);
        }

        private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
