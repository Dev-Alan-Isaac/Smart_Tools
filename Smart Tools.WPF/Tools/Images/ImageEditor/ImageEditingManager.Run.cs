using Smart_Tools.WPF.Models;
using System.Diagnostics;
using System.IO;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>Exporting a batch.</summary>
    public sealed partial class ImageEditingManager
    {
        /// <summary>Writes one image to a path. Tests swap this out; the default is Magick.NET (Imaging.cs).</summary>
        public Func<ImageItem, ImageEditOptions, string, CancellationToken, Task>? RenderOverride { get; set; }

        private static readonly object NameLock = new();

        /// <summary>
        /// Exports every selected image. One bad file no longer stops the
        /// batch (the old loop gave up at the first error and never marked
        /// the row); each row gets its own status and reason. Files are
        /// written under a temporary name and renamed when complete.
        /// </summary>
        public async Task<ImageEditResult> RunAsync(
            IReadOnlyList<ImageItem> items,
            ImageEditOptions o,
            IProgress<ImageEditProgress>? progress = null,
            CancellationToken token = default)
        {
            var clock = Stopwatch.StartNew();
            var result = new ImageEditResult();
            var work = items.Where(i => i.IsSelected && i.Status != ImageItemStatus.Unreadable).ToList();
            if (work.Count == 0) return result;

            Directory.CreateDirectory(o.OutputFolder);
            int parallel = o.MaxDegreeOfParallelism > 0 ? o.MaxDegreeOfParallelism : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var gate = new object();
            int done = 0;
            long lastReport = 0;

            foreach (var item in work)
            {
                item.Status = ImageItemStatus.Ready;
                item.Message = "";
            }

            await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = parallel }, async (item, _) =>
            {
                if (token.IsCancellationRequested)
                {
                    item.Status = ImageItemStatus.Cancelled;
                    item.Message = "Not started - the export was cancelled.";
                }
                else
                {
                    await ExportOneAsync(item, o, used, token).ConfigureAwait(false);
                }

                lock (gate)
                {
                    done++;
                    switch (item.Status)
                    {
                        case ImageItemStatus.Done:
                            result.Exported++;
                            result.OutputFiles.Add(item.OutputPath!);
                            try
                            {
                                result.OutputBytes += new FileInfo(item.OutputPath!).Length;
                            }
                            catch (IOException)
                            {
                                // gone already - just not counted
                            }
                            break;
                        case ImageItemStatus.Cancelled:
                            result.Cancelled++;
                            break;
                        default:
                            result.Failed++;
                            break;
                    }
                    long now = clock.ElapsedMilliseconds;
                    if (done == work.Count || now - lastReport >= 100)
                    {
                        lastReport = now;
                        progress?.Report(new ImageEditProgress(done, work.Count, item.Name));
                    }
                }
            }).ConfigureAwait(false);

            result.Elapsed = clock.Elapsed;
            return result;
        }

        private async Task ExportOneAsync(ImageItem item, ImageEditOptions o, HashSet<string> used, CancellationToken token)
        {
            string final;
            lock (NameLock) final = OutputPath(item.FullPath, o, used);
            string temp = final + ".part";
            item.Status = ImageItemStatus.Exporting;
            try
            {
                if (!File.Exists(item.FullPath)) throw new FileNotFoundException("The file is no longer there.");

                if (RenderOverride != null) await RenderOverride(item, o, temp, token).ConfigureAwait(false);
                else await Task.Run(() => RenderWithMagick(item, o, temp, token), token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();
                if (!File.Exists(temp) || new FileInfo(temp).Length == 0) throw new IOException("Nothing was written.");

                lock (NameLock)
                {
                    if (File.Exists(final)) final = OutputPath(item.FullPath, o, used);
                    File.Move(temp, final);
                }
                item.OutputPath = final;
                item.Status = ImageItemStatus.Done;
                item.Message = "Saved as " + final;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                item.Status = ImageItemStatus.Cancelled;
                item.Message = "Cancelled.";
            }
            catch (Exception ex)
            {
                item.Status = ImageItemStatus.Failed;
                item.Message = ex.Message;
            }
            finally
            {
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch (IOException)
                {
                    // best effort
                }
            }
        }
    }
}
