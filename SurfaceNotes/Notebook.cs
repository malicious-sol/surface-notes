using System;
using System.Linq;
using Windows.Storage;

namespace SurfaceNotes
{
    /// <summary>A notebook is a folder. Each page is one file inside it (see <see cref="PageStore"/>).</summary>
    public sealed class Notebook
    {
        /// <summary>
        /// Stable id. For a folder the user picked it is also the folder's FutureAccessList token,
        /// which is how the app keeps access to it across launches. In-app notebooks use "app:" + folder name.
        /// </summary>
        public string Id { get; set; }

        /// <summary>True when the notebook lives inside the app's own storage, where versions before 1.1 kept every notebook.</summary>
        public bool InApp { get; set; }

        public string Name { get; set; }

        /// <summary>Last known full path of the folder.</summary>
        public string Path { get; set; }

        /// <summary>Null when the folder can't be reached right now (moved, deleted, or on a drive that isn't connected).</summary>
        public StorageFolder Folder { get; set; }

        public bool IsAvailable { get { return Folder != null; } }

        public bool IsInOneDrive
        {
            get { return !InApp && Path != null && Path.IndexOf("\\OneDrive", StringComparison.OrdinalIgnoreCase) >= 0; }
        }

        public double ItemOpacity { get { return IsAvailable ? 1.0 : 0.55; } }

        /// <summary>Second line in the notebook list: where the notebook is kept.</summary>
        public string LocationText
        {
            get
            {
                if (!IsAvailable) return "Can't find this folder";
                if (InApp) return "This PC, inside the app";
                string parent = System.IO.Path.GetDirectoryName((Path ?? "").TrimEnd('\\'));
                return string.IsNullOrEmpty(parent) ? "This PC" : DescribeFolder(parent);
            }
        }

        /// <summary>
        /// A short, friendly description of a folder path:
        /// "OneDrive › School" for C:\Users\you\OneDrive\School, "This PC › Documents › Notes" otherwise.
        /// </summary>
        public static string DescribeFolder(string path)
        {
            const string Sep = " \u203A ";
            var parts = (path ?? "").Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count == 0) return "";
            int oneDrive = parts.FindIndex(p => p.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase));
            if (oneDrive >= 0) return string.Join(Sep, parts.Skip(oneDrive));
            // C:\Users\<name>\Documents\Notes -> Documents › Notes
            if (parts.Count > 3 && parts[1].Equals("Users", StringComparison.OrdinalIgnoreCase)) parts = parts.Skip(3).ToList();
            return "This PC" + Sep + string.Join(Sep, parts.Skip(Math.Max(0, parts.Count - 3)));
        }

        public override string ToString() { return Name; }
    }

    /// <summary>One page of a notebook: a file in the notebook's folder (or, for a brand-new empty page, a file that doesn't exist yet).</summary>
    public sealed class PageRef
    {
        public StorageFolder Folder { get; set; }
        public string FileName { get; set; }

        /// <summary>Null until the page is first saved.</summary>
        public StorageFile File { get; set; }

        public int Number { get; set; }
        public string Label { get; set; }

        // The version of the file this app last read or wrote. A different file on disk means
        // the page was changed somewhere else (another PC through OneDrive, for example).
        internal string KnownHash;
        internal ulong KnownSize;
        internal DateTimeOffset KnownModified;

        internal void Remember(PageVersion version)
        {
            KnownHash = version.Hash;
            KnownSize = version.Size;
            KnownModified = version.Modified;
        }

        public override string ToString() { return Label ?? ("Page " + Number); }
    }

    internal struct PageVersion
    {
        public string Hash;
        public ulong Size;
        public DateTimeOffset Modified;
    }
}
