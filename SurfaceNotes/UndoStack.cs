using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;
using Windows.UI.Input.Inking;

namespace SurfaceNotes
{
    /// <summary>Small undo history: strokes added, strokes removed, strokes moved.</summary>
    public sealed class UndoStack
    {
        private enum Kind { Added, Removed, Moved }

        private sealed class Entry
        {
            public Kind Kind;
            public List<InkStroke> Strokes;
            public Point Offset;
        }

        private const int MaxEntries = 200;
        private readonly List<Entry> entries = new List<Entry>();

        public bool CanUndo { get { return entries.Count > 0; } }

        public void Clear() { entries.Clear(); }

        public void PushAdded(IEnumerable<InkStroke> strokes) { Push(new Entry { Kind = Kind.Added, Strokes = strokes.ToList() }); }
        public void PushRemoved(IEnumerable<InkStroke> strokes) { Push(new Entry { Kind = Kind.Removed, Strokes = strokes.ToList() }); }
        public void PushMoved(IEnumerable<InkStroke> strokes, Point offset) { Push(new Entry { Kind = Kind.Moved, Strokes = strokes.ToList(), Offset = offset }); }

        private void Push(Entry e)
        {
            if (e.Strokes.Count == 0) return;
            entries.Add(e);
            if (entries.Count > MaxEntries) entries.RemoveAt(0);
        }

        public bool Undo(InkStrokeContainer container)
        {
            if (entries.Count == 0) return false;
            var e = entries[entries.Count - 1];
            entries.RemoveAt(entries.Count - 1);

            switch (e.Kind)
            {
                case Kind.Added:
                    SelectOnly(container, e.Strokes);
                    container.DeleteSelected();
                    break;

                case Kind.Removed:
                    // Removed strokes can't be re-added directly, so add copies and
                    // point older history entries at the copies.
                    var map = new Dictionary<InkStroke, InkStroke>();
                    foreach (var s in e.Strokes)
                    {
                        var copy = s.Clone();
                        map[s] = copy;
                        container.AddStroke(copy);
                    }
                    foreach (var older in entries)
                    {
                        for (int i = 0; i < older.Strokes.Count; i++)
                        {
                            InkStroke replacement;
                            if (map.TryGetValue(older.Strokes[i], out replacement)) older.Strokes[i] = replacement;
                        }
                    }
                    break;

                case Kind.Moved:
                    SelectOnly(container, e.Strokes);
                    container.MoveSelected(new Point(-e.Offset.X, -e.Offset.Y));
                    break;
            }

            foreach (var s in container.GetStrokes()) s.Selected = false;
            return true;
        }

        private static void SelectOnly(InkStrokeContainer container, List<InkStroke> strokes)
        {
            foreach (var s in container.GetStrokes()) s.Selected = false;
            foreach (var s in strokes) s.Selected = true;
        }
    }
}
