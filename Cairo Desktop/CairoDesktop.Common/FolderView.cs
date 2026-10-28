using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows.Data;
using System.Windows.Threading;
using ManagedShell.ShellFolders;

namespace CairoDesktop.Common
{
    public class FolderView
    {
        public ICollectionView DisplayItems;

        private readonly bool isDesktop;
        private readonly string path;
        private readonly HashSet<ShellFile> observedFiles = new HashSet<ShellFile>();
        private int refreshPending;

        private readonly string PublicDesktopPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory,
            Environment.SpecialFolderOption.DoNotVerify);

        public FolderView(ShellFolder location)
        {
            isDesktop = location?.IsDesktop == true;
            path = location?.Path;
            
            DisplayItems = GetCollectionView(location);
        }
        
        private ICollectionView GetCollectionView(ShellFolder location)
        {
            if (location == null)
            {
                return CollectionViewSource.GetDefaultView(new List<ShellItem>());
            }

            // Desktop and stacks can show the same folder. Keep their filters and sorting independent.
            ListCollectionView cvs = new ListCollectionView(location.Files);

            // Use DeferRefresh to batch all view changes and prevent intermediate UI updates
            using (cvs.DeferRefresh())
            {
                cvs.Filter = IconsSource_Filter;

                if (location.IsFileSystem)
                {
                    // ShellFile has no DateModified property to use in SortDescriptions.
                    cvs.CustomSort = new DateModifiedComparer();
                }
                else
                {
                    // non-filesystem: fall back to name sorting
                    cvs.SortDescriptions.Add(new SortDescription(nameof(ShellItem.DisplayName), ListSortDirection.Ascending));
                }
            } // DeferRefresh is disposed here, triggering a single refresh with all changes applied

            CollectionChangedEventManager.AddHandler(location.Files, Files_CollectionChanged);
            ObserveFiles(location.Files);
            return cvs;
        }

        private void ObserveFiles(IEnumerable<ShellFile> files)
        {
            foreach (ShellFile file in files)
            {
                if (observedFiles.Add(file))
                {
                    PropertyChangedEventManager.AddHandler(file, File_PropertyChanged, nameof(ShellItem.DisplayName));
                }
            }
        }

        private void Files_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (ShellFile file in observedFiles)
                {
                    PropertyChangedEventManager.RemoveHandler(file, File_PropertyChanged, nameof(ShellItem.DisplayName));
                }
                observedFiles.Clear();
                ObserveFiles((IEnumerable<ShellFile>)sender);
            }
            else
            {
                if (e.OldItems != null)
                {
                    foreach (ShellFile file in e.OldItems)
                    {
                        PropertyChangedEventManager.RemoveHandler(file, File_PropertyChanged, nameof(ShellItem.DisplayName));
                        observedFiles.Remove(file);
                    }
                }
                if (e.NewItems != null)
                {
                    foreach (ShellFile file in e.NewItems)
                    {
                        ObserveFiles(new[] { file });
                    }
                }
            }
        }

        private void File_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // ManagedShell's existing LastWrite watcher calls ShellItem.Refresh(), which raises
            // DisplayName even when only the timestamp changed. Live sorting on DisplayName
            // alone would miss that case because the bound name still has the same value.
            // Notifications can arrive on the shell worker thread; coalesce them on the UI thread.
            if (DisplayItems is ListCollectionView view && Interlocked.Exchange(ref refreshPending, 1) == 0)
            {
                view.Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
                {
                    Interlocked.Exchange(ref refreshPending, 0);
                    view.Refresh();
                }));
            }
        }

        private bool IconsSource_Filter(object obj)
        {
            if (obj is ShellFile file)
            {
                if (isDesktop)
                {
                    // the desktop folder includes some links to other folders which are also in the Places menu
                    if (!file.Path.StartsWith(path) && !file.Path.StartsWith(PublicDesktopPath))
                    {
                        return false;
                    }

                    // in newer Windows 11 builds, the Desktop also lists itself
                    if (file.Path == path)
                    {
                        return false;
                    }
                }

                if (file.Path == @"::{21EC2020-3AEA-1069-A2DD-08002B30309D}\::{98F2AB62-0E29-4E4C-8EE7-B542E66740B1}")
                {
                    // this is a mystery item in Control Panel
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Sort filesystem items by current modification time, newest first, then by name.
        /// </summary>
        private class DateModifiedComparer : IComparer
        {
            public int Compare(object x, object y)
            {
                if (ReferenceEquals(x, y))
                {
                    return 0;
                }

                if (x is ShellFile fileX && y is ShellFile fileY)
                {
                    DateTime dateX = GetDateModified(fileX);
                    DateTime dateY = GetDateModified(fileY);

                    // Compare newest first (descending)
                    int result = dateY.CompareTo(dateX);

                    // If dates are equal, sort by name
                    if (result == 0)
                    {
                        return string.Compare(fileX.DisplayName, fileY.DisplayName, StringComparison.CurrentCultureIgnoreCase);
                    }

                    return result;
                }

                // Fallback to name comparison for non-ShellFile items
                if (x is ShellItem itemX && y is ShellItem itemY)
                {
                    return string.Compare(itemX.DisplayName, itemY.DisplayName, StringComparison.CurrentCultureIgnoreCase);
                }

                return 0;
            }

            private DateTime GetDateModified(ShellFile file)
            {
                try
                {
                    // UTC preserves chronological order across daylight-saving transitions.
                    // Do not retain dates between comparisons: a write can change the order.
                    if (!string.IsNullOrEmpty(file.Path) && File.Exists(file.Path))
                    {
                        return File.GetLastWriteTimeUtc(file.Path);
                    }
                    else if (!string.IsNullOrEmpty(file.Path) && Directory.Exists(file.Path))
                    {
                        return Directory.GetLastWriteTimeUtc(file.Path);
                    }
                }
                catch (IOException)
                {
                    // The item may disappear while the view is being sorted.
                }
                catch (UnauthorizedAccessException)
                {
                    // Keep inaccessible items visible, after dated items.
                }
                catch (System.Security.SecurityException)
                {
                    // A restricted location may not allow metadata reads.
                }

                return DateTime.MinValue;
            }
        }
    }
}
