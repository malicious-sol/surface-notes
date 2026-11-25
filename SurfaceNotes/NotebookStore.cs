using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Input.Inking;

namespace SurfaceNotes
{
    /// <summary>A notebook is a folder; each page is one .isf ink file inside it.</summary>
    public sealed class Notebook
    {
        public string Name { get; set; }
        public StorageFolder Folder { get; set; }
        public override string ToString() { return Name; }
    }

    public sealed class PageRef
    {
        public StorageFile File { get; set; }
        public int Number { get; set; }
        public string FileName { get { return File.Name; } }
        public override string ToString() { return "Page " + Number; }
    }

    /// <summary>
    /// Stores everything under the app's local folder:
    ///   %LOCALAPPDATA%\Packages\SurfaceNotes.Local_...\LocalState\Notebooks\&lt;notebook&gt;\&lt;page&gt;.isf
    /// ISF (Ink Serialized Format) keeps the full pen data, including pressure.
    /// </summary>
    public sealed class NotebookStore
    {
        private const string PageExt = ".isf";

        private async Task<StorageFolder> RootAsync()
        {
            return await ApplicationData.Current.LocalFolder.CreateFolderAsync("Notebooks", CreationCollisionOption.OpenIfExists);
        }

        public async Task<List<Notebook>> GetNotebooksAsync()
        {
            var root = await RootAsync();
            var folders = await root.GetFoldersAsync();
            if (folders.Count == 0)
            {
                await root.CreateFolderAsync("My Notebook", CreationCollisionOption.OpenIfExists);
                folders = await root.GetFoldersAsync();
            }
            return folders
                .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(f => new Notebook { Name = f.Name, Folder = f })
                .ToList();
        }

        public async Task<Notebook> CreateNotebookAsync(string name)
        {
            var root = await RootAsync();
            var folder = await root.CreateFolderAsync(CleanName(name), CreationCollisionOption.GenerateUniqueName);
            return new Notebook { Name = folder.Name, Folder = folder };
        }

        public async Task<string> RenameNotebookAsync(Notebook notebook, string newName)
        {
            await notebook.Folder.RenameAsync(CleanName(newName), NameCollisionOption.GenerateUniqueName);
            return notebook.Folder.Name;
        }

        public async Task DeleteNotebookAsync(Notebook notebook)
        {
            await notebook.Folder.DeleteAsync(StorageDeleteOption.Default); // goes to the Recycle Bin
        }

        public async Task<List<PageRef>> GetPagesAsync(Notebook notebook)
        {
            var files = (await notebook.Folder.GetFilesAsync())
                .Where(f => f.Name.EndsWith(PageExt, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToList();
            if (files.Count == 0)
            {
                files.Add((await CreatePageAsync(notebook)).File);
            }
            var pages = new List<PageRef>();
            for (int i = 0; i < files.Count; i++)
            {
                pages.Add(new PageRef { File = files[i], Number = i + 1 });
            }
            return pages;
        }

        public async Task<PageRef> CreatePageAsync(Notebook notebook)
        {
            // Name by timestamp so pages sort in creation order.
            string name = DateTime.UtcNow.Ticks.ToString("D20") + PageExt;
            var file = await notebook.Folder.CreateFileAsync(name, CreationCollisionOption.GenerateUniqueName);
            return new PageRef { File = file };
        }

        public async Task DeletePageAsync(PageRef page)
        {
            await page.File.DeleteAsync(StorageDeleteOption.Default);
        }

        public async Task<InkStrokeContainer> LoadPageAsync(PageRef page)
        {
            var container = new InkStrokeContainer();
            var props = await page.File.GetBasicPropertiesAsync();
            if (props.Size == 0) return container;
            using (var stream = await page.File.OpenAsync(FileAccessMode.Read))
            using (var input = stream.GetInputStreamAt(0))
            {
                await container.LoadAsync(input);
            }
            return container;
        }

        public async Task SavePageAsync(PageRef page, InkStrokeContainer container)
        {
            var folder = await page.File.GetParentAsync();
            // Write to a temp file first so a crash mid-save never corrupts the page.
            var temp = await folder.CreateFileAsync(page.File.Name + ".tmp", CreationCollisionOption.ReplaceExisting);
            if (container.GetStrokes().Count > 0)
            {
                using (var stream = await temp.OpenAsync(FileAccessMode.ReadWrite))
                {
                    using (var output = stream.GetOutputStreamAt(0))
                    {
                        await container.SaveAsync(output);
                        await output.FlushAsync();
                    }
                }
            }
            string name = page.File.Name;
            await temp.MoveAndReplaceAsync(page.File);
            page.File = await folder.GetFileAsync(name);
        }

        private static string CleanName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(cleaned) ? "Notebook" : cleaned;
        }
    }
}
