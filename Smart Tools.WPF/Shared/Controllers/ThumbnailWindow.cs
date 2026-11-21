using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Loads thumbnails by scroll position for any ItemsControl (DataGrid, ListBox, ItemsControl):
    /// items whose container is near the viewport are loaded, items far away are released
    /// (thumbnail set back to null), so a list of thousands never holds thousands of bitmaps and
    /// nothing is left without a picture when the user scrolls to it.
    /// Works for virtualized and non-virtualized panels (it measures each realized container
    /// against the host's bounds, vertical and horizontal). All members run on the UI thread.
    /// Usage: create once, call <see cref="Refresh"/> after the items change, <see cref="Reset"/>
    /// when a new list replaces the old one, <see cref="Dispose"/> when the view closes.
    /// </summary>
    public sealed class ThumbnailWindow<T> : IDisposable where T : class
    {
        private const double LoadScreens = 1.5;   // load this far beyond the viewport (in viewport sizes)
        private const double KeepScreens = 4.0;   // release beyond this

        private readonly FrameworkElement _host;
        private readonly Func<IEnumerable<T>> _items;
        private readonly Func<T, object?> _get;
        private readonly Action<T, object?> _set;
        private readonly Func<T, CancellationToken, Task<object?>> _load;
        private readonly SemaphoreSlim _throttle;
        private readonly HashSet<T> _wanted = new();
        private readonly HashSet<T> _busy = new();
        private readonly HashSet<T> _failed = new();
        private readonly DispatcherTimer _timer;
        private CancellationTokenSource _cts = new();

        /// <summary>Items that must keep their picture whatever the scroll position (e.g. a drawer's group).</summary>
        public Func<T, bool>? Pinned { get; set; }

        /// <summary>False for files that never get a picture (skipped, not counted as failures).</summary>
        public Func<T, bool>? Supports { get; set; }

        public ThumbnailWindow(FrameworkElement host, Func<IEnumerable<T>> items, Func<T, object?> get, Action<T, object?> set,
            Func<T, CancellationToken, Task<object?>> load, int parallel = 4)
        {
            _host = host;
            _items = items;
            _get = get;
            _set = set;
            _load = load;
            _throttle = new SemaphoreSlim(parallel);
            _timer = new DispatcherTimer(DispatcherPriority.Background, host.Dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
            _timer.Tick += (_, _) => { _timer.Stop(); Update(); };

            host.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Refresh()));
            host.SizeChanged += (_, _) => Refresh();
            host.IsVisibleChanged += (_, _) => { if (host.IsVisible) Refresh(); };
        }

        /// <summary>Queues an update; waits until scrolling pauses.</summary>
        public void Refresh()
        {
            _timer.Stop();
            _timer.Start();
        }

        /// <summary>The list was replaced: forget everything, stop loads in flight.</summary>
        public void Reset()
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            _wanted.Clear();
            _failed.Clear();
            Refresh();
        }

        public void Dispose()
        {
            _timer.Stop();
            _cts.Cancel();
        }

        /// <summary>Loads one item now regardless of scroll position (it stays while <see cref="Pinned"/> says so).</summary>
        public void Request(IEnumerable<T> items)
        {
            foreach (var item in items) Start(item);
        }

        private bool StillNeeded(T item) => _wanted.Contains(item) || (Pinned?.Invoke(item) ?? false);

        private void Update()
        {
            if (!_host.IsVisible || _host.ActualWidth <= 0 || _host.ActualHeight <= 0) return;

            var viewport = new Rect(0, 0, _host.ActualWidth, _host.ActualHeight);
            var loadRect = Inflate(viewport, LoadScreens);
            var keepRect = Inflate(viewport, KeepScreens);

            var containers = new List<(T Item, Rect Bounds)>();
            Collect(_host, containers);

            _wanted.Clear();
            var keep = new HashSet<T>();
            var toLoad = new List<T>();
            foreach (var (item, bounds) in containers)
            {
                if (bounds.IntersectsWith(keepRect)) keep.Add(item);
                if (!bounds.IntersectsWith(loadRect)) continue;
                _wanted.Add(item);
                toLoad.Add(item);
            }
            if (keep.Count == 0 && containers.Count == 0) return;   // nothing realized yet: do not release anything

            foreach (var item in _items())
            {
                if (_get(item) == null || keep.Contains(item) || (Pinned?.Invoke(item) ?? false)) continue;
                _set(item, null);
            }

            foreach (var item in toLoad) Start(item);
        }

        private void Start(T item)
        {
            if (_get(item) != null || _busy.Contains(item) || _failed.Contains(item)) return;
            if (Supports != null && !Supports(item)) return;
            _busy.Add(item);
            _ = RunAsync(item, _cts.Token);
        }

        private async Task RunAsync(T item, CancellationToken token)
        {
            try
            {
                await _throttle.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                _busy.Remove(item);
                return;
            }

            try
            {
                if (token.IsCancellationRequested || _get(item) != null || !StillNeeded(item)) return;   // scrolled away while queued
                object? picture = await _load(item, token);
                if (token.IsCancellationRequested) return;
                if (picture == null) _failed.Add(item);
                else if (StillNeeded(item)) _set(item, picture);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // A broken file must not stop the others, and is not retried until the next Reset.
                Log.Info($"Thumbnail failed: {ex.Message}");
                _failed.Add(item);
            }
            finally
            {
                _busy.Remove(item);
                _throttle.Release();
            }
        }

        private static Rect Inflate(Rect r, double screens) =>
            new(r.X - r.Width * screens, r.Y - r.Height * screens, r.Width * (1 + 2 * screens), r.Height * (1 + 2 * screens));

        /// <summary>Realized item containers: the first element down each branch whose DataContext is a T.</summary>
        private void Collect(DependencyObject parent, List<(T, Rect)> found)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement { DataContext: T item } fe && fe.ActualWidth > 0 && fe.ActualHeight > 0 &&
                    !(parent is FrameworkElement { DataContext: T }))
                {
                    try
                    {
                        var bounds = fe.TransformToAncestor(_host).TransformBounds(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight));
                        found.Add((item, bounds));
                    }
                    catch (InvalidOperationException)
                    {
                        // not connected any more
                    }
                    continue;
                }
                Collect(child, found);
            }
        }
    }
}
