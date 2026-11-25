using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.AccessCache;

namespace SurfaceNotes
{
    /// <summary>
    /// The list of notebooks. A notebook is any folder:
    ///  - one the user picked (in OneDrive, Documents, a USB drive...). The app keeps access to it across
    ///    launches through Windows' FutureAccessList; the token is the notebook's id and the metadata holds
    ///    its name and path, so the list survives even if a folder goes missing.
    ///  - or a folder inside the app's own storage (LocalState\Notebooks), where versions before 1.1 kept
    ///    every notebook. These can be moved to a folder the user picks.
    /// Nothing about the list is stored inside the notebook folders, so they sync cleanly.
    /// </summary>
    internal sealed class NotebookLibrary
    {
        public const string InAppPrefix = "app:";
        private const string NotebookTag = "notebook";
        private const string LastLocationToken = "lastLocation";
        private const string StorageVersionKey = "storageVersion";
        public const string OfferMoveKey = "offerMoveToFolder";
        public const string CurrentNotebookKey = "currentNotebook";

        private static StorageItemAccessList Access { get { return StorageApplicationPermissions.FutureAccessList; } }
        private static IPropertySet Settings { get { return ApplicationData.Current.LocalSettings.Values; } }

        public static string PageKey(string notebookId) { return "page:" + notebookId; }

        private static Task<StorageFolder> InAppRootAsync()
        {
            return ApplicationData.Current.LocalFolder.CreateFolderAsync("Notebooks", CreationCollisionOption.OpenIfExists).AsTask();
        }

        // ------------------------------------------------------------------ loading

        public async Task<List<Notebook>> LoadAsync()
        {
            bool firstRun = !(Settings[StorageVersionKey] is int version && version >= 2);
            var list = new List<Notebook>();

            var root = await InAppRootAsync();
            foreach (var folder in await root.GetFoldersAsync())
            {
                if (firstRun) await AdoptLegacyNotebookAsync(folder);
                list.Add(InAppNotebook(folder));
            }

            foreach (var entry in Access.Entries.ToList())
            {
                var parts = (entry.Metadata ?? "").Split('|'); // "notebook|<name>|<path>" ('|' can't be in a name or path)
                if (parts[0] != NotebookTag) continue;
                var nb = new Notebook
                {
                    Id = entry.Token,
                    Name = parts.Length > 1 ? parts[1] : "Notebook",
                    Path = parts.Length > 2 ? parts[2] : null,
                };
                await ResolveAsync(nb);
                list.Add(nb);
            }

            if (firstRun)
            {
                if (list.Any(n => n.InApp)) Settings[OfferMoveKey] = true;
                Settings[StorageVersionKey] = 2;
            }

            if (list.Count == 0)
            {
                // First launch: start with one notebook so there's somewhere to write right away.
                list.Add(InAppNotebook(await root.CreateFolderAsync("My Notebook", CreationCollisionOption.OpenIfExists)));
            }
            return Sorted(list);
        }

        /// <summary>
        /// An in-app notebook for ink that couldn't be saved where it belongs (its folder went missing,
        /// for example), so leaving the page or closing the app never loses it.
        /// </summary>
        public async Task<Notebook> RecoveryNotebookAsync()
        {
            var root = await InAppRootAsync();
            return InAppNotebook(await root.CreateFolderAsync("Recovered pages", CreationCollisionOption.OpenIfExists));
        }

        public static List<Notebook> Sorted(IEnumerable<Notebook> notebooks)
        {
            return notebooks.OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static Notebook InAppNotebook(StorageFolder folder)
        {
            return new Notebook { Id = InAppPrefix + folder.Name, InApp = true, Name = folder.Name, Path = folder.Path, Folder = folder };
        }

        /// <summary>Older versions: convert the pages, and carry over which notebook and page were open.</summary>
        private static async Task AdoptLegacyNotebookAsync(StorageFolder folder)
        {
            Dictionary<string, string> renamed;
            try
            {
                renamed = await PageStore.ConvertLegacyPagesAsync(folder);
            }
            catch (Exception ex)
            {
                Log.Error("Converting notebook " + folder.Name, ex);
                return;
            }
            var lastNotebook = Settings["lastNotebook"] as string;
            var lastPage = Settings["lastPage"] as string;
            if (lastNotebook != folder.Name) return;
            string id = InAppPrefix + folder.Name;
            Settings[CurrentNotebookKey] = id;
            string newName;
            if (lastPage != null) Settings[PageKey(id)] = renamed.TryGetValue(lastPage, out newName) ? newName : lastPage;
        }

        /// <summary>Finds the notebook's folder. Leaves <see cref="Notebook.Folder"/> null if it can't be reached.</summary>
        public async Task ResolveAsync(Notebook nb)
        {
            try
            {
                if (nb.InApp)
                {
                    var root = await InAppRootAsync();
                    nb.Folder = await root.TryGetItemAsync(nb.Name) as StorageFolder;
                    return;
                }
                nb.Folder = Access.ContainsItem(nb.Id) ? await Access.GetFolderAsync(nb.Id) : null;
                if (nb.Folder != null && (nb.Folder.Name != nb.Name || nb.Folder.Path != nb.Path))
                {
                    Remember(nb, nb.Folder); // renamed or moved in File Explorer
                }
            }
            catch (Exception ex)
            {
                Log.Error("Opening notebook folder " + nb.Path, ex);
                nb.Folder = null;
            }
        }

        // ------------------------------------------------------------------ adding

        /// <summary>Makes a new notebook folder inside <paramref name="parent"/>.</summary>
        public async Task<Notebook> CreateAsync(string name, StorageFolder parent)
        {
            var folder = await parent.CreateFolderAsync(CleanName(name), CreationCollisionOption.GenerateUniqueName);
            return Add(folder);
        }

        /// <summary>Adds an existing folder as a notebook.</summary>
        public Notebook Add(StorageFolder folder)
        {
            var nb = new Notebook { Id = Guid.NewGuid().ToString("N") };
            Remember(nb, folder);
            return nb;
        }

        /// <summary>Points a notebook whose folder went missing at the folder the user found.</summary>
        public void Relocate(Notebook nb, StorageFolder folder)
        {
            Remember(nb, folder);
        }

        private static void Remember(Notebook nb, StorageFolder folder)
        {
            nb.Folder = folder;
            nb.Name = folder.Name;
            nb.Path = folder.Path;
            nb.InApp = false;
            Access.AddOrReplace(nb.Id, folder, NotebookTag + "|" + folder.Name + "|" + folder.Path);
        }

        public async Task<StorageFolder> GetLastLocationAsync()
        {
            try
            {
                return Access.ContainsItem(LastLocationToken) ? await Access.GetFolderAsync(LastLocationToken) : null;
            }
            catch
            {
                return null;
            }
        }

        public void SetLastLocation(StorageFolder folder)
        {
            Access.AddOrReplace(LastLocationToken, folder, "location");
        }

        // ------------------------------------------------------------------ changing

        public async Task RenameAsync(Notebook nb, string newName)
        {
            string name = CleanName(newName);
            await nb.Folder.RenameAsync(name, NameCollisionOption.FailIfExists);
            if (nb.InApp)
            {
                string oldId = nb.Id;
                var renamed = InAppNotebook(await (await InAppRootAsync()).GetFolderAsync(name));
                nb.Id = renamed.Id;
                nb.Name = renamed.Name;
                nb.Path = renamed.Path;
                nb.Folder = renamed.Folder;
                MoveSetting(PageKey(oldId), PageKey(nb.Id));
                if (Settings[CurrentNotebookKey] as string == oldId) Settings[CurrentNotebookKey] = nb.Id;
            }
            else
            {
                Remember(nb, nb.Folder); // the folder object now has its new name and path
            }
        }

        /// <summary>Takes a notebook off the list. Its folder and pages are left as they are.</summary>
        public void Remove(Notebook nb)
        {
            if (!nb.InApp && Access.ContainsItem(nb.Id)) Access.Remove(nb.Id);
            Settings.Remove(PageKey(nb.Id));
        }

        /// <summary>Deletes an in-app notebook. Its pages go to the Recycle Bin where Windows allows it.</summary>
        public async Task DeleteInAppAsync(Notebook nb)
        {
            if (!nb.InApp) throw new InvalidOperationException("Only notebooks stored inside the app are deleted by the app.");
            await nb.Folder.DeleteAsync(StorageDeleteOption.Default);
            Remove(nb);
        }

        /// <summary>
        /// Moves an in-app notebook into a folder the user picked: copies every page, checks each copy
        /// byte for byte, and only then removes the original. If anything fails, the original is untouched.
        /// </summary>
        public async Task<Notebook> MoveToFolderAsync(Notebook nb, StorageFolder parent)
        {
            var source = nb.Folder;
            var dest = await parent.CreateFolderAsync(nb.Name, CreationCollisionOption.GenerateUniqueName);
            try
            {
                foreach (var file in await source.GetFilesAsync())
                {
                    if (!PageStore.IsPageFile(file.Name)) continue;
                    var copy = await file.CopyAsync(dest, file.Name, NameCollisionOption.FailIfExists);
                    if (!await PageStore.SameContentAsync(file, copy)) throw new IOException("A page didn't copy correctly.");
                }
            }
            catch
            {
                try { await dest.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
                throw;
            }

            var moved = Add(dest);
            MoveSetting(PageKey(nb.Id), PageKey(moved.Id));
            if (Settings[CurrentNotebookKey] as string == nb.Id) Settings[CurrentNotebookKey] = moved.Id;
            try
            {
                await source.DeleteAsync(StorageDeleteOption.Default);
            }
            catch (Exception ex)
            {
                // The copies are complete. The leftover shows up as an in-app notebook and can be deleted.
                Log.Error("Removing moved notebook " + source.Path, ex);
            }
            return moved;
        }

        private static void MoveSetting(string from, string to)
        {
            object value;
            if (Settings.TryGetValue(from, out value))
            {
                Settings[to] = value;
                Settings.Remove(from);
            }
        }

        public static string CleanName(string name)
        {
            var cleaned = PageStore.SafeFileName(name);
            return string.IsNullOrWhiteSpace(cleaned) ? "Notebook" : cleaned;
        }
    }
}
