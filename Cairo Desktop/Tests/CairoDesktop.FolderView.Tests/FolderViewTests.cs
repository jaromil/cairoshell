using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CairoDesktop.Common;
using ManagedShell.ShellFolders;
using Xunit;

namespace CairoDesktop.Tests
{
    public class FolderViewTests
    {
        private static readonly DateTime Epoch = new DateTime(2025, 1, 2, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void FilesAndDirectoriesAreSortedByTimeBeforeName()
        {
            RunOnSta(() =>
            {
                using (var directory = new TestDirectory())
                {
                    directory.AddFile("a-old.txt", Epoch);
                    directory.AddFile("z-new.txt", Epoch.AddSeconds(2));
                    string subdirectory = Path.Combine(directory.Path, "b-middle");
                    Directory.CreateDirectory(subdirectory);
                    Directory.SetLastWriteTimeUtc(subdirectory, Epoch.AddSeconds(1));
                    using (var folder = directory.Open())
                    {
                        var view = new FolderView(folder);
                        Assert.Equal(new[] { "z-new.txt", "b-middle", "a-old.txt" }, Names(view));
                    }
                }
            });
        }

        [Fact]
        public void EqualTimestampsAreSortedByName()
        {
            RunOnSta(() =>
            {
                using (var directory = new TestDirectory())
                {
                    directory.AddFile("z.txt", Epoch);
                    directory.AddFile("a.txt", Epoch);
                    using (var folder = directory.Open())
                    {
                        Assert.Equal(new[] { "a.txt", "z.txt" }, Names(new FolderView(folder)));
                    }
                }
            });
        }

        [Fact]
        public void RefreshReadsNewTimestampsInsteadOfCachedDates()
        {
            RunOnSta(() =>
            {
                using (var directory = new TestDirectory())
                {
                    string oldFile = directory.AddFile("a.txt", Epoch);
                    directory.AddFile("z.txt", Epoch.AddSeconds(2));
                    using (var folder = directory.Open())
                    {
                        var view = new FolderView(folder);
                        Assert.Equal(new[] { "z.txt", "a.txt" }, Names(view));
                        File.SetLastWriteTimeUtc(oldFile, Epoch.AddSeconds(4));
                        view.DisplayItems.Refresh();
                        Assert.Equal(new[] { "a.txt", "z.txt" }, Names(view));
                    }
                }
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ShellRefreshResortsEvenWhenTheNameIsUnchanged(bool workerThread)
        {
            RunOnSta(() =>
            {
                using (var directory = new TestDirectory())
                {
                    string oldFile = directory.AddFile("a.txt", Epoch);
                    directory.AddFile("z.txt", Epoch.AddSeconds(2));
                    using (var folder = directory.Open())
                    {
                        var view = new FolderView(folder);
                        Assert.Equal(new[] { "z.txt", "a.txt" }, Names(view));
                        ShellFile file = folder.Files.Single(item => item.Path == oldFile);
                        string name = file.DisplayName;
                        File.SetLastWriteTimeUtc(oldFile, Epoch.AddSeconds(4));
                        if (workerThread)
                            Task.Run(() => file.Refresh()).GetAwaiter().GetResult();
                        else
                            file.Refresh();

                        WaitFor(() => Names(view).First() == "a.txt");
                        Assert.Equal(name, file.DisplayName);
                    }
                }
            });
        }

        [Fact]
        public void MissingFilesSortLastAndRecreatedPathsUseTheirNewTimestamp()
        {
            RunOnSta(() =>
            {
                using (var directory = new TestDirectory())
                {
                    string newest = directory.AddFile("z.txt", Epoch.AddSeconds(2));
                    directory.AddFile("a.txt", Epoch);
                    using (var folder = directory.Open())
                    {
                        var view = new FolderView(folder);
                        Assert.Equal(new[] { "z.txt", "a.txt" }, Names(view));
                        // Watchers are disabled here so the view still contains the vanished item.
                        File.Delete(newest);
                        view.DisplayItems.Refresh();
                        Assert.Equal(new[] { "a.txt", "z.txt" }, Names(view));
                        directory.AddFile("z.txt", Epoch.AddSeconds(4));
                        view.DisplayItems.Refresh();
                        Assert.Equal(new[] { "z.txt", "a.txt" }, Names(view));
                    }
                }
            });
        }

        [Fact]
        public void AddedAndResetItemsContinueToReceiveTimestampUpdates()
        {
            RunOnSta(() =>
            {
                using (var directory = new TestDirectory())
                {
                    directory.AddFile("z.txt", Epoch.AddSeconds(2));
                    using (var folder = directory.Open())
                    {
                        var view = new FolderView(folder);
                        string path = directory.AddFile("a.txt", Epoch);
                        var added = new ShellFile(folder, path);
                        folder.Files.Add(added);
                        Assert.Equal(new[] { "z.txt", "a.txt" }, Names(view));
                        File.SetLastWriteTimeUtc(path, Epoch.AddSeconds(4));
                        added.Refresh();
                        WaitFor(() => Names(view).First() == "a.txt");

                        ShellFile[] files = folder.Files.ToArray();
                        folder.Files.Clear();
                        Assert.Empty(Names(view));
                        foreach (ShellFile file in files)
                            folder.Files.Add(file);

                        File.SetLastWriteTimeUtc(path, Epoch);
                        added.Refresh();
                        WaitFor(() => Names(view).First() == "z.txt");
                        folder.Files.Remove(added);
                        added.Dispose();
                        Assert.Equal(new[] { "z.txt" }, Names(view));
                    }
                }
            });
        }

        [Fact]
        public void ExistingFileWatcherResortsTheViewAfterAWrite()
        {
            RunOnSta(() =>
            {
                using (var directory = new TestDirectory())
                {
                    string oldFile = directory.AddFile("a.txt", Epoch);
                    directory.AddFile("z.txt", Epoch.AddSeconds(2));
                    using (var folder = directory.Open(watchChanges: true))
                    {
                        var view = new FolderView(folder);
                        Assert.Equal(new[] { "z.txt", "a.txt" }, Names(view));
                        File.AppendAllText(oldFile, "updated");
                        WaitFor(() => Names(view).First() == "a.txt");
                    }
                }
            });
        }

        [Fact]
        public void TwoViewsOfTheSameFolderHaveIndependentSorting()
        {
            RunOnSta(() =>
            {
                using (var directory = new TestDirectory())
                {
                    directory.AddFile("a.txt", Epoch);
                    directory.AddFile("z.txt", Epoch.AddSeconds(2));
                    using (var folder = directory.Open())
                    {
                        var first = new FolderView(folder);
                        var second = new FolderView(folder);
                        Assert.NotSame(first.DisplayItems, second.DisplayItems);
                        first.DisplayItems.Filter = item => false;
                        Assert.Empty(Names(first));
                        Assert.Equal(new[] { "z.txt", "a.txt" }, Names(second));
                    }
                }
            });
        }

        [Fact]
        public void NullLocationProducesAnEmptyView()
        {
            RunOnSta(() => Assert.Empty(Names(new FolderView(null))));
        }

        private static string[] Names(FolderView view)
        {
            return view.DisplayItems.Cast<ShellFile>().Select(file => Path.GetFileName(file.Path)).ToArray();
        }

        private static void WaitFor(Func<bool> condition)
        {
            var frame = new DispatcherFrame();
            var elapsed = Stopwatch.StartNew();
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(10)
            };
            timer.Tick += (sender, args) =>
            {
                if (condition() || elapsed.Elapsed > TimeSpan.FromSeconds(10))
                    frame.Continue = false;
            };
            timer.Start();
            try
            {
                Dispatcher.PushFrame(frame);
                Assert.True(condition(), "The folder view did not reach the expected order within 10 seconds.");
            }
            finally
            {
                timer.Stop();
            }
        }

        private static void RunOnSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception exception) { failure = exception; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The STA test thread timed out.");
            if (failure != null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private sealed class TestDirectory : IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CairoFolderView-" + Guid.NewGuid());

            public TestDirectory() { Directory.CreateDirectory(Path); }

            public string AddFile(string name, DateTime modifiedUtc)
            {
                string path = System.IO.Path.Combine(Path, name);
                File.WriteAllText(path, "test");
                File.SetLastWriteTimeUtc(path, modifiedUtc);
                return path;
            }

            public ShellFolder Open(bool watchChanges = false)
            {
                return new ShellFolder(Path, IntPtr.Zero, false, watchChanges);
            }

            public void Dispose() { Directory.Delete(Path, true); }
        }
    }
}
