using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Input.Inking;

namespace SurfaceNotes
{
    internal enum SaveStatus { Saved, Unchanged, ConflictCopy }

    internal sealed class SaveResult
    {
        public SaveStatus Status;

        /// <summary>For <see cref="SaveStatus.ConflictCopy"/>: the new page that holds this PC's version.</summary>
        public PageRef Copy;
    }

    internal enum DiskState { Same, Changed, Missing }

    internal sealed class LoadedPage
    {
        public InkStrokeContainer Strokes;
        public PageVersion Version;
    }

    /// <summary>
    /// Reads and writes pages. Each page is one file in the notebook's folder:
    ///
    ///   2026-11-25 141830.gif
    ///
    /// The file is a GIF picture of the page with the full pen data (Ink Serialized Format, with pressure)
    /// embedded in it. File Explorer, OneDrive and any image viewer show it as a picture, and Windows Ink
    /// apps (this one included) load the ink back without loss. Pages are named by when they were made,
    /// so they sort in order, and there is no shared index file that two PCs could both change.
    ///
    /// Saves are written to a temporary file and then swapped in, so a crash never leaves half a page.
    /// Before each save the file on disk is checked against the version this app last saw; if it was
    /// changed elsewhere (another PC through OneDrive), both versions are kept instead of overwriting one.
    /// </summary>
    internal static class PageStore
    {
        public const string PageExt = ".gif";
        public const string LegacyExt = ".isf"; // versions before 1.1
        private const string TempExt = ".tmp";

        // A 1x1 transparent GIF, saved for blank pages so the file is still a valid picture.
        private static readonly byte[] BlankGif =
        {
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xFF, 0xFF, 0xFF, 0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x01, 0x44, 0x00, 0x3B,
        };

        private static readonly HashAlgorithmProvider Sha256 = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);

        // "2026-11-25 141830" (this version) or 20 digits (older versions), optionally followed by more text,
        // such as "-LAPTOP" when OneDrive keeps a second copy after two PCs changed the same page.
        private static readonly Regex TimeStamped = new Regex(@"^(\d{4}-\d{2}-\d{2} \d{6}|\d{20})(.*)$");

        public static bool IsPageFile(string name)
        {
            return name.EndsWith(PageExt, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(LegacyExt, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ listing

        public static async Task<List<PageRef>> ListAsync(StorageFolder folder)
        {
            var files = await folder.GetFilesAsync();
            var pages = files
                .Where(f => IsPageFile(f.Name))
                // By name without the extension, so "<page>-LAPTOP.gif" (a copy) comes right after "<page>.gif".
                .OrderBy(f => Path.GetFileNameWithoutExtension(f.Name), StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new PageRef { Folder = folder, FileName = f.Name, File = f })
                .ToList();
            Renumber(pages);
            return pages;
        }

        public static void Renumber(List<PageRef> pages)
        {
            for (int i = 0; i < pages.Count; i++)
            {
                pages[i].Number = i + 1;
                pages[i].Label = MakeLabel(pages[i].FileName, i + 1);
            }
        }

        private static string MakeLabel(string fileName, int number)
        {
            string stem = Path.GetFileNameWithoutExtension(fileName);
            var match = TimeStamped.Match(stem);
            if (!match.Success) return stem; // a page someone named themselves in File Explorer
            string rest = match.Groups[2].Value.Trim();
            if (rest.Length == 0) return "Page " + number;
            if (rest.StartsWith("-", StringComparison.Ordinal)) rest = "copy from " + rest.Substring(1).Trim();
            return "Page " + number + " (" + rest.Trim('(', ')', ' ') + ")";
        }

        /// <summary>A page that isn't saved yet: the file is created the first time there's ink to save.</summary>
        public static PageRef NewBlankPage(StorageFolder folder)
        {
            return new PageRef { Folder = folder, FileName = NewPageName(DateTime.Now), Number = 1, Label = "Page 1", KnownHash = "" };
        }

        private static string NewPageName(DateTime time)
        {
            return time.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture) + PageExt;
        }

        /// <summary>Creates a new blank page file that sorts after the existing pages.</summary>
        public static async Task<PageRef> CreateAsync(StorageFolder folder)
        {
            var now = DateTime.Now;
            for (int i = 0; i < 60; i++)
            {
                string name = NewPageName(now.AddSeconds(i));
                if (await folder.TryGetItemAsync(name) != null) continue;
                var data = BlankGif.AsBuffer();
                var file = await WriteAtomicAsync(folder, name, data);
                var page = new PageRef { Folder = folder, FileName = file.Name, File = file };
                page.Remember(await VersionOfAsync(file, Hash(data)));
                return page;
            }
            throw new IOException("Couldn't pick a name for the new page.");
        }

        public static async Task DeleteAsync(PageRef page)
        {
            var file = await page.Folder.TryGetItemAsync(page.FileName) as StorageFile;
            if (file != null) await file.DeleteAsync(StorageDeleteOption.Default); // to the Recycle Bin
        }

        // ------------------------------------------------------------------ loading

        public static async Task<LoadedPage> LoadAsync(PageRef page)
        {
            var result = new LoadedPage { Strokes = new InkStrokeContainer(), Version = new PageVersion { Hash = "" } };
            if (page.File == null) return result; // not saved yet

            // Properties first: if the file changes while it's being read, the next check notices.
            var props = await page.File.GetBasicPropertiesAsync();
            var data = await FileIO.ReadBufferAsync(page.File);
            if (data.Length > 0 && !IsBlank(data))
            {
                using (var stream = new InMemoryRandomAccessStream())
                {
                    await stream.WriteAsync(data);
                    using (var input = stream.GetInputStreamAt(0))
                    {
                        await result.Strokes.LoadAsync(input);
                    }
                }
            }
            result.Version = new PageVersion { Hash = Hash(data), Size = props.Size, Modified = props.DateModified };
            return result;
        }

        /// <summary>Whether the page's file still matches the version this app last read or wrote.</summary>
        public static async Task<DiskState> CheckAsync(PageRef page)
        {
            if (page.File == null) return DiskState.Same; // never saved, nothing to compare
            var file = await page.Folder.TryGetItemAsync(page.FileName) as StorageFile;
            if (file == null) return DiskState.Missing;
            page.File = file;
            return await ChangedElsewhereAsync(page, file) ? DiskState.Changed : DiskState.Same;
        }

        private static async Task<bool> ChangedElsewhereAsync(PageRef page, StorageFile file)
        {
            var props = await file.GetBasicPropertiesAsync();
            if (page.KnownHash != null && props.Size == page.KnownSize && props.DateModified == page.KnownModified) return false;
            // The date or size moved. Compare the content, since syncing can touch a file without changing it.
            string hash = Hash(await FileIO.ReadBufferAsync(file));
            if (hash != page.KnownHash) return true;
            page.KnownSize = props.Size;
            page.KnownModified = props.DateModified;
            return false;
        }

        // ------------------------------------------------------------------ saving

        public static async Task<SaveResult> SaveAsync(PageRef page, InkStrokeContainer strokes)
        {
            IBuffer data = await SerializeAsync(strokes);
            string hash = Hash(data);

            var onDisk = await page.Folder.TryGetItemAsync(page.FileName) as StorageFile;
            if (onDisk != null && await ChangedElsewhereAsync(page, onDisk))
            {
                // Changed somewhere else since this app read it. Keep both: the other version stays
                // as it is, and this PC's version is saved next to it as a copy.
                var copy = await WriteConflictCopyAsync(page, data, hash);
                return new SaveResult { Status = SaveStatus.ConflictCopy, Copy = copy };
            }
            if (onDisk != null && hash == page.KnownHash)
            {
                page.File = onDisk;
                return new SaveResult { Status = SaveStatus.Unchanged }; // no write, so nothing for OneDrive to upload
            }

            // (If the file was deleted elsewhere while this page had new ink, it is written again.)
            var file = await WriteAtomicAsync(page.Folder, page.FileName, data);
            page.File = file;
            page.Remember(await VersionOfAsync(file, hash));
            return new SaveResult { Status = SaveStatus.Saved };
        }

        private static async Task<PageRef> WriteConflictCopyAsync(PageRef page, IBuffer data, string hash)
        {
            // Same naming as OneDrive's own copies: "<page>-<PC name>.gif".
            string stem = Path.GetFileNameWithoutExtension(page.FileName) + "-" + SafeFileName(DeviceName());
            string name = stem + PageExt;
            for (int i = 2; await page.Folder.TryGetItemAsync(name) != null; i++) name = stem + " " + i + PageExt;
            var file = await WriteAtomicAsync(page.Folder, name, data);
            var copy = new PageRef { Folder = page.Folder, FileName = file.Name, File = file };
            copy.Remember(await VersionOfAsync(file, hash));
            return copy;
        }

        private static async Task<PageVersion> VersionOfAsync(StorageFile file, string hash)
        {
            var props = await file.GetBasicPropertiesAsync();
            return new PageVersion { Hash = hash, Size = props.Size, Modified = props.DateModified };
        }

        private static async Task<IBuffer> SerializeAsync(InkStrokeContainer strokes)
        {
            if (strokes.GetStrokes().Count == 0) return BlankGif.AsBuffer();
            try
            {
                return await SerializeAsync(strokes, InkPersistenceFormat.GifWithEmbeddedIsf);
            }
            catch (Exception ex)
            {
                // If the picture can't be made, still save the ink itself. This app reads it either way.
                Log.Error("Saving as a picture failed, saved ink only", ex);
                return await SerializeAsync(strokes, InkPersistenceFormat.Isf);
            }
        }

        private static async Task<IBuffer> SerializeAsync(InkStrokeContainer strokes, InkPersistenceFormat format)
        {
            using (var stream = new InMemoryRandomAccessStream())
            {
                using (var output = stream.GetOutputStreamAt(0))
                {
                    await strokes.SaveAsync(output, format);
                    await output.FlushAsync();
                }
                uint size = (uint)stream.Size;
                using (var input = stream.GetInputStreamAt(0))
                {
                    return await input.ReadAsync(new Windows.Storage.Streams.Buffer(size), size, InputStreamOptions.None);
                }
            }
        }

        // ------------------------------------------------------------------ files

        /// <summary>
        /// Writes the whole file to "&lt;name&gt;.tmp" and then renames it over &lt;name&gt;, so the page on disk is
        /// always either the old version or the new one, never a mix. OneDrive sees one finished file.
        /// </summary>
        public static async Task<StorageFile> WriteAtomicAsync(StorageFolder folder, string name, IBuffer data)
        {
            var temp = await folder.CreateFileAsync(name + TempExt, CreationCollisionOption.ReplaceExisting);
            try
            {
                using (var stream = await temp.OpenAsync(FileAccessMode.ReadWrite))
                {
                    using (var output = stream.GetOutputStreamAt(0))
                    {
                        await output.WriteAsync(data);
                        await output.FlushAsync();
                    }
                }
                await temp.RenameAsync(name, NameCollisionOption.ReplaceExisting);
                return temp;
            }
            catch
            {
                try { await temp.DeleteAsync(StorageDeleteOption.PermanentDelete); } catch { }
                throw;
            }
        }

        public static async Task<bool> SameContentAsync(StorageFile a, StorageFile b)
        {
            var x = await FileIO.ReadBufferAsync(a);
            var y = await FileIO.ReadBufferAsync(b);
            return x.Length == y.Length && Hash(x) == Hash(y);
        }

        private static string Hash(IBuffer data)
        {
            if (data == null || data.Length == 0) return "";
            return CryptographicBuffer.EncodeToBase64String(Sha256.HashData(data));
        }

        private static bool IsBlank(IBuffer data)
        {
            return data.Length == BlankGif.Length && data.ToArray().SequenceEqual(BlankGif);
        }

        private static bool IsGif(IBuffer data)
        {
            if (data.Length < 6) return false;
            var head = data.ToArray(0, 3);
            return head[0] == 'G' && head[1] == 'I' && head[2] == 'F';
        }

        private static string DeviceName()
        {
            try
            {
                var name = new EasClientDeviceInformation().FriendlyName;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
            return "this PC";
        }

        public static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string((name ?? "").Where(c => !invalid.Contains(c)).ToArray()).Trim().TrimEnd('.');
            return cleaned;
        }

        // ------------------------------------------------------------------ pages from older versions

        /// <summary>
        /// Old pages were named by creation time in ticks ("0639…"). They get the same readable kind of name as
        /// new pages ("2026-11-25 141830.gif"), which keeps them in order and before any page made later.
        /// </summary>
        private static async Task<string> LegacyNewNameAsync(StorageFolder folder, string stem)
        {
            long ticks;
            if (stem.Length == 20 && long.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out ticks)
                && ticks > DateTime.MinValue.Ticks && ticks < DateTime.MaxValue.Ticks)
            {
                var made = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
                for (int i = 0; i < 10; i++)
                {
                    string name = NewPageName(made.AddSeconds(i));
                    if (await folder.TryGetItemAsync(name) == null) return name;
                }
            }
            string plain = stem + PageExt;
            return await folder.TryGetItemAsync(plain) == null ? plain : null;
        }

        /// <summary>
        /// Versions before 1.1 saved pages as "&lt;ticks&gt;.isf". Those files already held a GIF with the ink
        /// inside, so they are just renamed (see <see cref="LegacyNewNameAsync"/>); any that hold plain ink data are
        /// converted, and the old file is only removed after the new one is written. Safe to run again if interrupted.
        /// Returns old name -> new name.
        /// </summary>
        public static async Task<Dictionary<string, string>> ConvertLegacyPagesAsync(StorageFolder folder)
        {
            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in await folder.GetFilesAsync())
            {
                string oldName = file.Name;
                if (!oldName.EndsWith(LegacyExt, StringComparison.OrdinalIgnoreCase)) continue;
                string stem = Path.GetFileNameWithoutExtension(oldName);
                try
                {
                    string newName = await LegacyNewNameAsync(folder, stem);
                    if (newName == null) continue; // a .gif with this name is already there: keep both; both show as pages
                    var data = await FileIO.ReadBufferAsync(file);
                    if (data.Length == 0 || IsGif(data))
                    {
                        await file.RenameAsync(newName, NameCollisionOption.FailIfExists);
                    }
                    else
                    {
                        var strokes = new InkStrokeContainer();
                        using (var stream = new InMemoryRandomAccessStream())
                        {
                            await stream.WriteAsync(data);
                            using (var input = stream.GetInputStreamAt(0))
                            {
                                await strokes.LoadAsync(input);
                            }
                        }
                        await WriteAtomicAsync(folder, newName, await SerializeAsync(strokes));
                        await file.DeleteAsync(StorageDeleteOption.Default);
                    }
                    renamed[oldName] = newName;
                }
                catch (Exception ex)
                {
                    // The page still opens under its old name.
                    Log.Error("Converting " + oldName, ex);
                }
            }
            return renamed;
        }
    }
}
