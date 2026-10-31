using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Smart_Tools.WPF.Models
{
    // Shared by every tool with potentially huge lists (Audio Replay,
    // Background Remover, ...). Moved out of AudioReplayModel.cs so tools
    // don't depend on each other's model files.
    /// <summary>
    /// ObservableCollection that can add/remove many items with ONE change
    /// notification. Adding a 10,000-file folder item-by-item used to raise
    /// 10,000 CollectionChanged events, each making the DataGrid do layout
    /// work - the main reason big folders felt slow to appear. Small
    /// batches still raise normal per-item events so the grid keeps its
    /// scroll position and selection.
    /// </summary>
    public sealed class BulkObservableCollection<T> : ObservableCollection<T>
    {
        private const int ResetThreshold = 50;

        public void AddRange(IReadOnlyCollection<T> items)
        {
            if (items.Count == 0)
                return;

            if (items.Count < ResetThreshold)
            {
                foreach (var item in items)
                    Add(item);
                return;
            }

            CheckReentrancy();
            foreach (var item in items)
                Items.Add(item);
            RaiseReset();
        }

        /// <summary>Removes every item matching the predicate. Returns how many were removed.</summary>
        public int RemoveWhere(Func<T, bool> predicate)
        {
            var toRemove = Items.Where(predicate).ToList();
            if (toRemove.Count == 0)
                return 0;

            if (toRemove.Count < ResetThreshold)
            {
                foreach (var item in toRemove)
                    Remove(item);
                return toRemove.Count;
            }

            CheckReentrancy();
            var keep = Items.Where(i => !predicate(i)).ToList();
            Items.Clear();
            foreach (var item in keep)
                Items.Add(item);
            RaiseReset();
            return toRemove.Count;
        }

        private void RaiseReset()
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
