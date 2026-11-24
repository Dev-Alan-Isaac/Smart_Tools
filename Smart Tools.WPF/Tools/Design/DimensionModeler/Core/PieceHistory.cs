using Smart_Tools.WPF.Models;

namespace Smart_Tools.WPF.Models
{
    /// <summary>A custom piece at one moment: what undo and redo swap back in.</summary>
    public sealed record PieceState(string Name, IReadOnlyList<Part> Parts);
}

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Undo and redo for the custom piece. Every change pushes the state it started from; nothing is edited in place.</summary>
    public sealed class PieceHistory
    {
        public const int Limit = 100;

        private readonly List<PieceState> _undo = new();
        private readonly List<PieceState> _redo = new();

        public bool CanUndo => _undo.Count > 0;

        public bool CanRedo => _redo.Count > 0;

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
        }

        private static bool Same(PieceState a, PieceState b) =>
            a.Name == b.Name && a.Parts.SequenceEqual(b.Parts);

        /// <summary>Call with the state as it is BEFORE a change. A change that leaves it the same is not recorded.</summary>
        public void Push(PieceState before, PieceState after)
        {
            if (Same(before, after)) return;
            _undo.Add(before);
            if (_undo.Count > Limit) _undo.RemoveAt(0);
            _redo.Clear();
        }

        /// <summary>The state to go back to, or null; <paramref name="current"/> becomes the redo step.</summary>
        public PieceState? Undo(PieceState current)
        {
            if (_undo.Count == 0) return null;
            var back = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(current);
            return back;
        }

        public PieceState? Redo(PieceState current)
        {
            if (_redo.Count == 0) return null;
            var forward = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(current);
            return forward;
        }
    }
}
