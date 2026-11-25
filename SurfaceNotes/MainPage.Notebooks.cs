using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Search;
using Windows.UI.Core;
using Windows.UI.Input.Inking;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace SurfaceNotes
{
    /// <summary>Opening, saving and keeping track of notebooks and pages.</summary>
    public sealed partial class MainPage
    {
        // Save this long after writing stops, and at least this often while writing goes on.
        // Saving at every short pause would make OneDrive upload constantly, which costs battery and heat.
        private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan MaxUnsaved = TimeSpan.FromSeconds(20);

        private readonly NotebookLibrary library = new NotebookLibrary();
        private readonly SemaphoreSlim saveLock = new SemaphoreSlim(1, 1);
        private readonly DispatcherTimer saveTimer = new DispatcherTimer();
        private readonly DispatcherTimer refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };

        private List<Notebook> notebooks = new List<Notebook>();
        private Notebook currentNotebook;
        private List<PageRef> pages = new List<PageRef>();
        private PageRef currentPage;
        private bool pageLoaded; // false when the page on screen couldn't be read: it is then never saved over
        private bool dirty;
        private DateTime dirtySince;
        private int saveFailures;
        private int navTicket;
        private int busyCount;
        private bool ignoreListEvents;
        private DateTime lastActivationCheck;

        private StorageFolder watchedFolder;
        private StorageFileQueryResult folderQuery;
        private TypedEventHandler<IStorageQueryResultBase, object> folderChangedHandler;

        private bool busy { get { return busyCount > 0; } }

        private static IPropertySet Settings { get { return ApplicationData.Current.LocalSettings.Values; } }

        private void InitNotebooks()
        {
            saveTimer.Tick += async (s, e) => { saveTimer.Stop(); await SaveNowAsync(); };
            refreshTimer.Tick += async (s, e) => { refreshTimer.Stop(); await RefreshFromDiskAsync(); };
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Window.Current.Activated += Window_Activated;
            Window.Current.VisibilityChanged += Window_VisibilityChanged;
            try
            {
                notebooks = await library.LoadAsync(); // the first time, this also brings over notes from older versions
                string id = Settings[NotebookLibrary.CurrentNotebookKey] as string;
                var start = notebooks.FirstOrDefault(n => n.Id == id && n.IsAvailable) ?? notebooks.FirstOrDefault(n => n.IsAvailable);
                if (start != null) await OpenNotebookAsync(start, null);
                else ShowNoNotebook();
                await OfferMoveToFolderAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Starting up", ex);
                ShowLists();
                ShowNotice("Something went wrong opening your notebooks: " + FriendlyError(ex) + ".", "error");
            }
        }

        // ---------------------------------------------------------------- opening

        private async Task OpenNotebookAsync(Notebook nb, string preferPage)
        {
            await SaveNowAsync(); // so a page that was just saved for the first time is in the list below
            if (!nb.IsAvailable) await library.ResolveAsync(nb);
            if (!nb.IsAvailable)
            {
                ShowLists(); // put the list selection back
                await HandleMissingNotebookAsync(nb);
                return;
            }

            List<PageRef> list;
            try
            {
                list = await PageStore.ListAsync(nb.Folder);
            }
            catch (Exception ex)
            {
                Log.Error("Listing pages in " + nb.Path, ex);
                ShowLists();
                ShowNotice("Couldn't open \u201C" + nb.Name + "\u201D: " + FriendlyError(ex) + ".", "error");
                return;
            }
            if (list.Count == 0) list.Add(PageStore.NewBlankPage(nb.Folder));

            string remembered = preferPage ?? Settings[NotebookLibrary.PageKey(nb.Id)] as string;
            var target = list.FirstOrDefault(p => SameName(p.FileName, remembered)) ?? list[list.Count - 1];
            await OpenPageAsync(nb, list, target);
        }

        private async Task OpenPageAsync(Notebook nb, List<PageRef> list, PageRef page)
        {
            int ticket = ++navTicket;
            LoadedPage loaded = null;
            Exception loadError = null;
            try
            {
                loaded = await PageStore.LoadAsync(page);
            }
            catch (Exception ex)
            {
                loadError = ex;
                Log.Error("Opening page " + page.FileName, ex);
            }
            if (ticket != navTicket) return; // a later tap won

            await saveLock.WaitAsync();
            try
            {
                if (ticket != navTicket) return;
                // Save the page being left. Repeat if more ink arrived during the save.
                for (int i = 0; dirty && i < 3; i++) await SaveCoreAsync();
                if (dirty && !await RescueAsync())
                {
                    ShowLists();
                    ShowNotice("This page couldn't be saved yet, so it stays open. Try again in a moment.", "save");
                    return;
                }
                // Nothing below awaits, so the ink on screen and the file it saves to always change together.
                ShowPage(nb, list, page, loaded, loadError);
            }
            finally
            {
                saveLock.Release();
            }
        }

        private void ShowPage(Notebook nb, List<PageRef> list, PageRef page, LoadedPage loaded, Exception loadError)
        {
            ClearSelection();
            undo.Clear();
            currentNotebook = nb;
            pages = list;
            currentPage = page;
            dirty = false;
            pageLoaded = loaded != null;
            if (loaded != null) page.Remember(loaded.Version);
            Ink.InkPresenter.StrokeContainer = loaded != null ? loaded.Strokes : new InkStrokeContainer();
            Ink.InkPresenter.IsInputEnabled = pageLoaded && !busy;

            ResetInkExtent();
            UpdateSheetSize(allowShrink: true);
            Scroller.ChangeView(0, 0, null, true);
            ShowTitle();
            ShowLists();
            WatchFolder(nb.Folder);

            HideNotice("page");
            HideNotice("nonotebook");
            if (loadError != null) ShowPageError(loadError);

            Settings[NotebookLibrary.CurrentNotebookKey] = nb.Id;
            Settings[NotebookLibrary.PageKey(nb.Id)] = page.FileName;
        }

        private void ShowPageError(Exception ex)
        {
            string text = ex is System.IO.FileNotFoundException
                ? "This page's file is missing. It may have been moved or deleted."
                : "Couldn't open this page, so it's left untouched. If it's in OneDrive, check that OneDrive is running and you're online.";
            ShowNotice(text, "page", false, "Try again", () => currentNotebook != null
                ? OpenNotebookAsync(currentNotebook, currentPage != null ? currentPage.FileName : null)
                : Task.CompletedTask);
        }

        private void ShowNoNotebook()
        {
            currentNotebook = null;
            currentPage = null;
            pages = new List<PageRef>();
            pageLoaded = false;
            dirty = false;
            undo.Clear();
            Ink.InkPresenter.StrokeContainer = new InkStrokeContainer();
            Ink.InkPresenter.IsInputEnabled = false;
            TitleText.Text = "";
            ShowLists();
            WatchFolder(null);
            Split.IsPaneOpen = true;
            ShowNotice("Pick a notebook in the side panel, or make one with +.", "nonotebook");
        }

        private void ShowTitle()
        {
            TitleText.Text = currentNotebook != null && currentPage != null ? currentNotebook.Name + "  \u00B7  " + currentPage : "";
        }

        private void ShowLists()
        {
            ignoreListEvents = true;
            try
            {
                if (!ReferenceEquals(NotebookList.ItemsSource, notebooks)) NotebookList.ItemsSource = notebooks;
                NotebookList.SelectedItem = currentNotebook;
                if (!ReferenceEquals(PageList.ItemsSource, pages)) PageList.ItemsSource = pages;
                PageList.SelectedItem = currentPage;
            }
            finally
            {
                ignoreListEvents = false;
            }
        }

        private async void NotebookList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ignoreListEvents) return;
            var nb = NotebookList.SelectedItem as Notebook;
            if (nb == null || nb == currentNotebook) return;
            if (busy) { ShowLists(); return; }
            await RunAsync(() => OpenNotebookAsync(nb, null));
        }

        private async void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ignoreListEvents) return;
            var page = PageList.SelectedItem as PageRef;
            if (page == null || page == currentPage || currentNotebook == null) return;
            if (busy) { ShowLists(); return; }
            var nb = currentNotebook;
            var list = pages;
            await RunAsync(() => OpenPageAsync(nb, list, page));
        }

        private void PaneButton_Click(object sender, RoutedEventArgs e)
        {
            Split.IsPaneOpen = !Split.IsPaneOpen;
            if (Split.IsPaneOpen) ScheduleRefresh(); // show pages added on other PCs
        }

        // ---------------------------------------------------------------- pages

        private async void AddPage_Click(object sender, RoutedEventArgs e)
        {
            if (busy || currentNotebook == null || !currentNotebook.IsAvailable) return;
            var nb = currentNotebook;
            await RunAsync(async () =>
            {
                await SaveNowAsync();
                var page = await PageStore.CreateAsync(nb.Folder);
                await OpenNotebookAsync(nb, page.FileName);
            });
        }

        private void PageList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var page = (e.OriginalSource as FrameworkElement)?.DataContext as PageRef;
            if (page == null || busy) return;
            var menu = new MenuFlyout();
            AddMenuItem(menu, "Delete page", () => DeletePageAsync(page));
            menu.ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
        }

        private async Task DeletePageAsync(PageRef page)
        {
            if (!await ConfirmAsync("Delete " + page + "?", "The page goes to the Recycle Bin.", "Delete")) return;
            var nb = currentNotebook;
            if (nb == null) return;
            string keep;
            await saveLock.WaitAsync();
            try
            {
                await SaveCoreAsync(); // even for the page being deleted, so nothing is lost if the delete fails
                await PageStore.DeleteAsync(page);
                if (page == currentPage)
                {
                    // Stop writing into it until the next page is open.
                    pageLoaded = false;
                    Ink.InkPresenter.IsInputEnabled = false;
                    int index = pages.IndexOf(page);
                    var next = pages.Where(p => p != page).ElementAtOrDefault(Math.Max(0, index - 1));
                    keep = next != null ? next.FileName : null;
                }
                else
                {
                    keep = currentPage != null ? currentPage.FileName : null;
                }
            }
            finally
            {
                saveLock.Release();
            }
            await OpenNotebookAsync(nb, keep);
        }

        // ---------------------------------------------------------------- notebooks

        private void NotebookList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var nb = (e.OriginalSource as FrameworkElement)?.DataContext as Notebook;
            if (nb == null || busy) return;
            var menu = new MenuFlyout();
            if (!nb.IsAvailable) AddMenuItem(menu, "Find it\u2026", () => LocateNotebookAsync(nb));
            if (nb.IsAvailable) AddMenuItem(menu, "Rename", () => RenameNotebookAsync(nb));
            if (nb.IsAvailable && nb.InApp) AddMenuItem(menu, "Move to a folder\u2026", () => MoveNotebooksAsync(new List<Notebook> { nb }));
            if (nb.IsAvailable && !nb.InApp) AddMenuItem(menu, "Show in File Explorer", () => Windows.System.Launcher.LaunchFolderAsync(nb.Folder).AsTask());
            // The app only deletes notebooks it keeps itself. A folder you picked is yours: it can be
            // taken off the list here, and deleted in File Explorer.
            if (!nb.InApp) AddMenuItem(menu, "Remove from list", () => RemoveNotebookAsync(nb));
            if (nb.InApp) AddMenuItem(menu, "Delete notebook", () => DeleteNotebookAsync(nb));
            menu.ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
        }

        private async void NewNotebook_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            await RunAsync(NewNotebookAsync);
        }

        private async Task NewNotebookAsync()
        {
            string name = "";
            var location = await library.GetLastLocationAsync();
            while (true)
            {
                var answer = await AskNewNotebookAsync(name, location);
                if (answer == null) return;
                name = answer.Name;
                if (answer.ChangeLocation || location == null)
                {
                    var picked = await PickFolderAsync("Save notebook here");
                    if (picked != null)
                    {
                        location = picked;
                        library.SetLastLocation(picked);
                    }
                    if (answer.ChangeLocation || location == null) continue; // show the dialog again, with the place picked
                }
                break;
            }

            await SaveNowAsync();
            var nb = await library.CreateAsync(string.IsNullOrWhiteSpace(name) ? "Notebook" : name, location);
            notebooks = NotebookLibrary.Sorted(notebooks.Concat(new[] { nb }));
            await OpenNotebookAsync(nb, null);
        }

        private async void OpenNotebookFolder_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            await RunAsync(OpenNotebookFolderAsync);
        }

        private async Task OpenNotebookFolderAsync()
        {
            var folder = await PickFolderAsync("Open notebook");
            if (folder == null) return;
            var known = FindNotebook(folder);
            if (known != null)
            {
                await OpenNotebookAsync(known, null);
                return;
            }

            var files = await folder.GetFilesAsync();
            if (!files.Any(f => PageStore.IsPageFile(f.Name)))
            {
                // A folder that holds notebooks (like the one they were moved into)? Offer to add them all.
                var inside = new List<StorageFolder>();
                foreach (var sub in (await folder.GetFoldersAsync()).Take(100))
                {
                    var subFiles = await sub.GetFilesAsync(CommonFileQuery.DefaultQuery, 0, 500);
                    if (subFiles.Any(f => PageStore.IsPageFile(f.Name))) inside.Add(sub);
                }
                if (inside.Count > 0)
                {
                    string names = string.Join(", ", inside.Take(8).Select(f => f.Name)) + (inside.Count > 8 ? ", \u2026" : "");
                    string title = inside.Count == 1
                        ? "Add the notebook in \u201C" + folder.Name + "\u201D?"
                        : "Add the " + inside.Count + " notebooks in \u201C" + folder.Name + "\u201D?";
                    if (!await ConfirmAsync(title, names, "Add", "Cancel", true)) return;
                    Notebook first = null;
                    var added = new List<Notebook>();
                    foreach (var sub in inside)
                    {
                        var nb = FindNotebook(sub);
                        if (nb == null)
                        {
                            nb = library.Add(sub);
                            added.Add(nb);
                        }
                        if (first == null) first = nb;
                    }
                    notebooks = NotebookLibrary.Sorted(notebooks.Concat(added));
                    await OpenNotebookAsync(first, null);
                    return;
                }
            }

            var notebook = library.Add(folder);
            notebooks = NotebookLibrary.Sorted(notebooks.Concat(new[] { notebook }));
            await OpenNotebookAsync(notebook, null);
        }

        private Notebook FindNotebook(StorageFolder folder)
        {
            string path = folder.Path.TrimEnd('\\');
            return notebooks.FirstOrDefault(n => n.Path != null && string.Equals(n.Path.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase));
        }

        private async Task RenameNotebookAsync(Notebook nb)
        {
            var name = await AskTextAsync("Rename notebook", nb.Name, "Rename");
            if (string.IsNullOrEmpty(name) || name == nb.Name) return;
            BeginBusy();
            try
            {
                await SaveNowAsync();
                if (nb == currentNotebook) WatchFolder(null); // let go of the folder while it's renamed
                await library.RenameAsync(nb, name);
                notebooks = NotebookLibrary.Sorted(notebooks);
                // The open page's file is at a new path now.
                if (nb == currentNotebook) await OpenNotebookAsync(nb, currentPage != null ? currentPage.FileName : null);
                else ShowLists();
            }
            finally
            {
                EndBusy();
            }
        }

        private async Task LocateNotebookAsync(Notebook nb)
        {
            var folder = await PickFolderAsync("Use this folder");
            if (folder == null) return;
            library.Relocate(nb, folder);
            notebooks = NotebookLibrary.Sorted(notebooks);
            await OpenNotebookAsync(nb, null);
        }

        private async Task HandleMissingNotebookAsync(Notebook nb)
        {
            var dialog = new ContentDialog
            {
                Title = "Can't find \u201C" + nb.Name + "\u201D",
                Content = new TextBlock
                {
                    Text = "Its folder may have been moved, renamed or deleted, or it's on a drive that isn't connected." +
                           (string.IsNullOrEmpty(nb.Path) ? "" : "\n\nIt was in " + nb.Path),
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Find it\u2026",
                SecondaryButtonText = "Remove from list",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await ShowDialogAsync(dialog);
            if (result == ContentDialogResult.Primary) await LocateNotebookAsync(nb);
            else if (result == ContentDialogResult.Secondary) await RemoveNotebookAsync(nb);
        }

        private async Task RemoveNotebookAsync(Notebook nb)
        {
            BeginBusy();
            try
            {
                await SaveNowAsync();
                library.Remove(nb);
                notebooks = NotebookLibrary.Sorted(notebooks.Where(n => n != nb));
                if (nb == currentNotebook) await OpenFirstAvailableAsync();
                else ShowLists();
            }
            finally
            {
                EndBusy();
            }
            ShowNotice("Removed \u201C" + nb.Name + "\u201D from the list. Its folder and pages weren't changed.", "info", true);
        }

        private async Task DeleteNotebookAsync(Notebook nb)
        {
            if (!await ConfirmAsync("Delete \u201C" + nb.Name + "\u201D?", "The notebook and all its pages go to the Recycle Bin.", "Delete")) return;
            BeginBusy();
            try
            {
                await SaveNowAsync();
                if (nb == currentNotebook) WatchFolder(null);
                await library.DeleteInAppAsync(nb);
                notebooks = NotebookLibrary.Sorted(notebooks.Where(n => n != nb));
                if (nb == currentNotebook)
                {
                    pageLoaded = false; // its files are gone; don't write them back
                    await OpenFirstAvailableAsync();
                }
                else
                {
                    ShowLists();
                }
            }
            finally
            {
                EndBusy();
            }
        }

        private async Task OpenFirstAvailableAsync()
        {
            if (notebooks.Count == 0) notebooks = await library.LoadAsync(); // starts a new "My Notebook"
            var next = notebooks.FirstOrDefault(n => n.IsAvailable);
            if (next != null) await OpenNotebookAsync(next, null);
            else ShowNoNotebook();
        }

        /// <summary>Moves notebooks kept inside the app to a folder the user picks (for example in OneDrive).</summary>
        private async Task MoveNotebooksAsync(List<Notebook> toMove)
        {
            var parent = await PickFolderAsync("Move notebooks here");
            if (parent == null) return;
            library.SetLastLocation(parent);

            int moved = 0;
            var failed = new List<string>();
            BeginBusy();
            ShowNotice("Moving your notebooks\u2026", "busy");
            try
            {
                await SaveNowAsync();
                if (toMove.Contains(currentNotebook)) WatchFolder(null);
                Notebook reopen = null;
                string reopenPage = null;
                var list = notebooks.ToList();
                foreach (var nb in toMove)
                {
                    try
                    {
                        var movedNb = await library.MoveToFolderAsync(nb, parent);
                        list.Remove(nb);
                        list.Add(movedNb);
                        moved++;
                        if (nb == currentNotebook)
                        {
                            reopen = movedNb;
                            reopenPage = currentPage != null ? currentPage.FileName : null;
                            pageLoaded = false; // the old files are gone; don't write them back
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Moving notebook " + nb.Name, ex);
                        failed.Add("\u201C" + nb.Name + "\u201D");
                    }
                }
                notebooks = NotebookLibrary.Sorted(list);
                if (reopen != null) await OpenNotebookAsync(reopen, reopenPage);
                else ShowLists();
            }
            finally
            {
                EndBusy();
            }

            string where = Notebook.DescribeFolder(parent.Path);
            if (failed.Count == 0)
            {
                ShowNotice((moved == 1 ? "Moved 1 notebook" : "Moved " + moved + " notebooks") + " to " + where + ".", "info", true);
            }
            else
            {
                ShowNotice("Couldn't move " + string.Join(", ", failed) + ", so " + (failed.Count == 1 ? "it's" : "they're") +
                           " still inside the app and nothing was lost." + (moved > 0 ? " The others moved to " + where + "." : ""), "error");
            }
        }

        private async Task OfferMoveToFolderAsync()
        {
            if (!(Settings[NotebookLibrary.OfferMoveKey] is bool offer && offer)) return;
            Settings.Remove(NotebookLibrary.OfferMoveKey); // ask once
            var inApp = notebooks.Where(n => n.InApp && n.IsAvailable).ToList();
            if (inApp.Count == 0) return;
            bool move = await ConfirmAsync(
                "Keep your notebooks in OneDrive?",
                "Notebooks can now live in any folder you choose, such as one in OneDrive, so they're backed up and synced like your other files.\n\n" +
                "Your notebooks are stored inside the app right now. Choose a folder to move them to, and each notebook becomes a folder of pages there.\n\n" +
                "You can also do this later: press and hold a notebook in the side panel, then choose Move to a folder.",
                "Choose folder\u2026", "Not now", true);
            if (move) await MoveNotebooksAsync(inApp);
        }

        private static async Task<StorageFolder> PickFolderAsync(string commitText)
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SettingsIdentifier = "notebookFolders",
                CommitButtonText = commitText,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(PageStore.PageExt); // shows existing pages, so notebook folders are easy to spot
            return await picker.PickSingleFolderAsync();
        }

        // ---------------------------------------------------------------- saving

        private void MarkDirty()
        {
            if (currentPage == null || !pageLoaded) return;
            var now = DateTime.UtcNow;
            if (!dirty) dirtySince = now;
            dirty = true;
            saveTimer.Stop();
            saveTimer.Interval = now - dirtySince >= MaxUnsaved ? TimeSpan.FromMilliseconds(50) : SaveDelay;
            saveTimer.Start();
        }

        public async Task SaveNowAsync()
        {
            saveTimer.Stop();
            await saveLock.WaitAsync();
            try
            {
                await SaveCoreAsync();
            }
            finally
            {
                saveLock.Release();
            }
        }

        /// <summary>Saves the page on screen if it has changes. The caller holds saveLock.</summary>
        private async Task SaveCoreAsync()
        {
            if (!dirty || currentPage == null || !pageLoaded) return;
            var page = currentPage;
            var strokes = Strokes;
            dirty = false;
            SaveResult result;
            try
            {
                result = await PageStore.SaveAsync(page, strokes);
            }
            catch (Exception ex)
            {
                dirty = true;
                saveFailures++;
                Log.Error("Saving " + page.FileName, ex);
                // A single failure is often OneDrive briefly holding the file; only mention it if it keeps happening.
                if (saveFailures >= 2) ShowNotice("Couldn't save this page yet (" + FriendlyError(ex) + "). Trying again\u2026", "save");
                saveTimer.Stop();
                saveTimer.Interval = TimeSpan.FromSeconds(Math.Min(30, 3 * saveFailures));
                saveTimer.Start();
                return;
            }
            if (saveFailures > 0)
            {
                saveFailures = 0;
                HideNotice("save");
            }
            if (result.Status == SaveStatus.ConflictCopy) await SwitchToCopyAsync(result.Copy);
        }

        /// <summary>
        /// Last resort when the page can't be saved where it belongs (its folder is gone, the drive was removed...):
        /// saves the ink as a page of the in-app "Recovered pages" notebook instead. The caller holds saveLock.
        /// </summary>
        private async Task<bool> RescueAsync()
        {
            if (!dirty || currentPage == null || !pageLoaded) return true;
            try
            {
                var recovery = await library.RecoveryNotebookAsync();
                var page = await PageStore.CreateAsync(recovery.Folder);
                await PageStore.SaveAsync(page, Strokes);
                dirty = false;
                if (!notebooks.Any(n => n.Id == recovery.Id)) notebooks = NotebookLibrary.Sorted(notebooks.Concat(new[] { recovery }));
                string from = currentNotebook != null ? "\u201C" + currentNotebook.Name + "\u201D" : "its notebook";
                ShowNotice("Couldn't save this page to " + from + ", so it was saved in \u201C" + recovery.Name + "\u201D (inside the app) instead.", "rescue");
                Log.Write("Rescued " + currentPage.FileName + " to " + page.FileName);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Rescuing a page", ex);
                return false;
            }
        }

        /// <summary>Used when the app is closing: save, and if that can't be done, keep the ink in "Recovered pages".</summary>
        public async Task SaveOrRescueAsync()
        {
            saveTimer.Stop();
            await saveLock.WaitAsync();
            try
            {
                await SaveCoreAsync();
                if (dirty) await SaveCoreAsync();
                if (dirty) await RescueAsync();
            }
            finally
            {
                saveLock.Release();
            }
        }

        /// <summary>After a save found the page changed elsewhere: carry on in the copy that holds this PC's version.</summary>
        private async Task SwitchToCopyAsync(PageRef copy)
        {
            List<PageRef> listed;
            try
            {
                listed = await PageStore.ListAsync(copy.Folder);
            }
            catch (Exception ex)
            {
                Log.Error("Listing after a conflict", ex);
                listed = pages.ToList();
            }
            var merged = MergePages(listed, copy);
            if (!merged.Contains(copy))
            {
                merged.Add(copy);
                PageStore.Renumber(merged);
            }
            pages = merged;
            currentPage = copy;
            ShowTitle();
            ShowLists();
            if (currentNotebook != null) Settings[NotebookLibrary.PageKey(currentNotebook.Id)] = copy.FileName;
            ShowNotice("This page was also changed somewhere else (another PC or app), so both versions are kept. You're on yours: " + copy + ".", "conflict");
        }

        /// <summary>A fresh listing, reusing the page objects already in use: they remember the version last read or written.</summary>
        private List<PageRef> MergePages(List<PageRef> listed, PageRef extra = null)
        {
            var known = pages.ToList();
            if (extra != null) known.Add(extra);
            var merged = new List<PageRef>();
            foreach (var p in listed)
            {
                var existing = known.FirstOrDefault(k => SameName(k.FileName, p.FileName));
                if (existing != null)
                {
                    existing.File = p.File;
                    merged.Add(existing);
                }
                else
                {
                    merged.Add(p);
                }
            }
            PageStore.Renumber(merged);
            return merged;
        }

        private static string PageListSignature(List<PageRef> list)
        {
            return string.Join("|", list.Select(p => p.FileName + ":" + p.Label));
        }

        private static bool SameName(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- changes made elsewhere

        private void Window_Activated(object sender, WindowActivatedEventArgs e)
        {
            if (e.WindowActivationState == CoreWindowActivationState.Deactivated)
            {
                var ignored = SaveNowAsync(); // leaving the app: save now rather than in a few seconds
                return;
            }
            if ((DateTime.UtcNow - lastActivationCheck).TotalSeconds < 5) return;
            lastActivationCheck = DateTime.UtcNow;
            ScheduleRefresh();
        }

        private void Window_VisibilityChanged(object sender, VisibilityChangedEventArgs e)
        {
            if (!e.Visible)
            {
                var ignored = SaveNowAsync();
            }
        }

        private void ScheduleRefresh()
        {
            refreshTimer.Stop();
            refreshTimer.Start();
        }

        /// <summary>
        /// Watches the open notebook's folder, so pages added, changed or removed elsewhere (another PC
        /// through OneDrive, File Explorer) show up. Windows notifies the app; nothing polls.
        /// </summary>
        private void WatchFolder(StorageFolder folder)
        {
            if (folder == watchedFolder) return;
            if (folderQuery != null) folderQuery.ContentsChanged -= folderChangedHandler;
            folderQuery = null;
            folderChangedHandler = null;
            watchedFolder = folder;
            if (folder == null) return;
            try
            {
                var options = new QueryOptions(CommonFileQuery.DefaultQuery, new[] { PageStore.PageExt, PageStore.LegacyExt })
                {
                    FolderDepth = FolderDepth.Shallow,
                    IndexerOption = IndexerOption.DoNotUseIndexer,
                };
                var query = folder.CreateFileQueryWithOptions(options);
                folderChangedHandler = (s, a) =>
                {
                    var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                    {
                        if (query != folderQuery) return;
                        ScheduleRefresh();
                        ArmWatcher(query);
                    });
                };
                query.ContentsChanged += folderChangedHandler;
                folderQuery = query;
                ArmWatcher(query);
            }
            catch (Exception ex)
            {
                Log.Error("Watching " + folder.Path, ex);
                folderQuery = null;
            }
        }

        private static async void ArmWatcher(StorageFileQueryResult query)
        {
            // Windows reports changes only after the query has run, so run it (again) each time.
            try { await query.GetFilesAsync(0, 1); } catch { }
        }

        private async Task RefreshFromDiskAsync()
        {
            if (busy || currentNotebook == null || !currentNotebook.IsAvailable || currentPage == null) return;
            PageRef goTo = null;
            bool reopen = false;
            await saveLock.WaitAsync();
            try
            {
                var nb = currentNotebook;
                var listed = await PageStore.ListAsync(nb.Folder);
                if (nb != currentNotebook || busy || currentPage == null) return;

                string before = PageListSignature(pages);
                var merged = MergePages(listed);
                bool listedNow = merged.Contains(currentPage);
                if (!listedNow)
                {
                    if (currentPage.File == null || dirty)
                    {
                        // Not saved yet, or removed elsewhere while it has new ink: keep it. The next save writes it.
                        merged.Add(currentPage);
                        PageStore.Renumber(merged);
                    }
                    else
                    {
                        // Removed or renamed elsewhere: go to the page next to where it was.
                        int index = Math.Max(0, pages.IndexOf(currentPage));
                        goTo = merged.Count > 0 ? merged[Math.Min(index, merged.Count - 1)] : null;
                        reopen = true;
                    }
                }
                pages = merged;
                if (PageListSignature(merged) != before)
                {
                    PageList.ItemsSource = null; // the labels may have changed on the same items

                    ShowLists();
                    ShowTitle();
                }

                if (listedNow && pageLoaded && !dirty && await PageStore.CheckAsync(currentPage) == DiskState.Changed)
                {
                    // Changed elsewhere and nothing new here: show the new version.
                    var page = currentPage;
                    var loaded = await PageStore.LoadAsync(page);
                    if (page == currentPage && !dirty)
                    {
                        ClearSelection();
                        undo.Clear();
                        page.Remember(loaded.Version);
                        Ink.InkPresenter.StrokeContainer = loaded.Strokes;
                        ResetInkExtent();
                        UpdateSheetSize(allowShrink: false);
                        ShowNotice("This page was updated with changes made somewhere else.", "info", true);
                    }
                    // If ink was added meanwhile, the next save sees the change and keeps both versions.
                }
            }
            catch (Exception ex)
            {
                Log.Error("Checking for changes", ex);
            }
            finally
            {
                saveLock.Release();
            }

            if (reopen && currentNotebook != null)
            {
                if (goTo != null) await OpenPageAsync(currentNotebook, pages, goTo);
                else await OpenNotebookAsync(currentNotebook, null);
            }
        }

        // ---------------------------------------------------------------- helpers

        private void BeginBusy()
        {
            busyCount++;
            Ink.InkPresenter.IsInputEnabled = false;
            Toolbar.IsHitTestVisible = false;
        }

        private void EndBusy()
        {
            busyCount = Math.Max(0, busyCount - 1);
            if (busy) return;
            Ink.InkPresenter.IsInputEnabled = pageLoaded;
            Toolbar.IsHitTestVisible = true;
            HideNotice("busy");
            // Folder operations stop watching the open notebook; pick it up again if one failed partway.
            if (currentNotebook != null && currentNotebook.IsAvailable) WatchFolder(currentNotebook.Folder);
        }

        private async Task RunAsync(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Log.Error("Action failed", ex);
                ShowNotice("That didn't work: " + FriendlyError(ex) + ".", "error");
            }
        }

        private void AddMenuItem(MenuFlyout menu, string text, Func<Task> action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += async (s, a) => await RunAsync(action);
            menu.Items.Add(item);
        }

        private static string FriendlyError(Exception ex)
        {
            switch ((uint)ex.HResult)
            {
                case 0x800700B7: // ERROR_ALREADY_EXISTS
                case 0x80070050: // ERROR_FILE_EXISTS
                    return "there's already something with that name there";
                case 0x80070005: // E_ACCESSDENIED
                    return "Windows didn't allow access to that folder";
                case 0x80070070: // ERROR_DISK_FULL
                    return "the disk is full";
                case 0x80070020: // ERROR_SHARING_VIOLATION
                case 0x80070021: // ERROR_LOCK_VIOLATION
                    return "another program is using the file";
                case 0x80070002: // ERROR_FILE_NOT_FOUND
                case 0x80070003: // ERROR_PATH_NOT_FOUND
                    return "the file or folder isn't there anymore";
            }
            return (ex.Message ?? "unknown error").Trim().TrimEnd('.');
        }
    }
}
