using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.Management.Deployment;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Input.Inking;
using Windows.UI.Input.Inking.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;

namespace SurfaceNotes
{
    public sealed partial class MainPage : Page
    {
        private enum Tool { Pen, Highlighter, Eraser, Lasso }

        private const double LineSpacing = 40;

        private readonly NotebookStore store = new NotebookStore();
        private readonly UndoStack undo = new UndoStack();
        private readonly SemaphoreSlim saveLock = new SemaphoreSlim(1, 1);
        private readonly DispatcherTimer saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        private readonly DispatcherTimer penLeftTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };

        private Notebook currentNotebook;
        private PageRef currentPage;
        private bool dirty;
        private bool ignoreListEvents;

        private Tool tool = Tool.Pen;
        private Color penColor = Color.FromArgb(255, 0x11, 0x11, 0x11);
        private double penSize = 3;

        // Lasso state
        private Polyline lassoLine;
        private List<Point> lassoPoints;
        private Rect selectionRect = Rect.Empty;
        private bool movingSelection;
        private Point lastMovePoint;
        private Point totalMove;

        private CoreInkIndependentInputSource penHover;

        private InkStrokeContainer Strokes { get { return Ink.InkPresenter.StrokeContainer; } }

        public MainPage()
        {
            InitializeComponent();

            var presenter = Ink.InkPresenter;
            // Palm rejection, part 1: touch never draws. Fingers pan and pinch-zoom the page instead.
            presenter.InputDeviceTypes = CoreInputDeviceTypes.Pen | CoreInputDeviceTypes.Mouse;
            // Holding the pen's side button (or right-dragging the mouse) lassos, in any tool.
            presenter.InputProcessingConfiguration.RightDragAction = InkInputRightDragAction.LeaveUnprocessed;

            presenter.StrokesCollected += Presenter_StrokesCollected;
            presenter.StrokesErased += Presenter_StrokesErased;
            presenter.StrokeInput.StrokeStarted += (s, e) => ClearSelection();
            presenter.UnprocessedInput.PointerPressed += Lasso_PointerPressed;
            presenter.UnprocessedInput.PointerMoved += Lasso_PointerMoved;
            presenter.UnprocessedInput.PointerReleased += Lasso_PointerReleased;

            // Palm rejection, part 2: while the pen hovers over the page, a resting palm
            // can't scroll or zoom it either.
            try
            {
                penHover = CoreInkIndependentInputSource.Create(presenter);
                penHover.PointerEntering += PenHover_Entering;
                penHover.PointerExiting += PenHover_Exiting;
            }
            catch
            {
                penHover = null; // Older Windows builds: touch still never draws.
            }
            penLeftTimer.Tick += (s, e) => { penLeftTimer.Stop(); SetTouchScrolling(true); };

            saveTimer.Tick += async (s, e) => { saveTimer.Stop(); await SaveNowAsync(); };

            ApplyTool();

            // In portrait the toolbar is tight, so drop the page title there (the side panel shows it).
            SizeChanged += (s, e) => TitleText.Visibility = e.NewSize.Width >= 1350 ? Visibility.Visible : Visibility.Collapsed;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var settings = ApplicationData.Current.LocalSettings.Values;
            await LoadNotebooksAsync(settings["lastNotebook"] as string, settings["lastPage"] as string);
        }

        // ---------------------------------------------------------------- notebooks & pages

        private async Task LoadNotebooksAsync(string selectNotebook, string selectPage)
        {
            var notebooks = await store.GetNotebooksAsync();
            var target = notebooks.FirstOrDefault(n => n.Name == selectNotebook) ?? notebooks[0];

            ignoreListEvents = true;
            NotebookList.ItemsSource = notebooks;
            NotebookList.SelectedItem = target;
            ignoreListEvents = false;

            await OpenNotebookAsync(target, selectPage);
        }

        private async Task OpenNotebookAsync(Notebook notebook, string selectPage)
        {
            await SaveNowAsync();
            currentNotebook = notebook;
            var pages = await store.GetPagesAsync(notebook);
            var target = pages.FirstOrDefault(p => p.FileName == selectPage) ?? pages[pages.Count - 1];

            ignoreListEvents = true;
            PageList.ItemsSource = pages;
            PageList.SelectedItem = target;
            ignoreListEvents = false;

            await OpenPageAsync(target);
        }

        private async Task OpenPageAsync(PageRef page)
        {
            await SaveNowAsync();
            ClearSelection();
            undo.Clear();

            currentPage = page;
            try
            {
                Ink.InkPresenter.StrokeContainer = await store.LoadPageAsync(page);
            }
            catch (Exception ex)
            {
                Ink.InkPresenter.StrokeContainer = new InkStrokeContainer();
                await ShowMessageAsync("Couldn't open this page", ex.Message);
            }

            TitleText.Text = currentNotebook.Name + "  ·  " + page;
            UpdateSheetSize(allowShrink: true);
            Scroller.ChangeView(0, 0, null, true);

            var settings = ApplicationData.Current.LocalSettings.Values;
            settings["lastNotebook"] = currentNotebook.Name;
            settings["lastPage"] = page.FileName;
        }

        private async void NotebookList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ignoreListEvents) return;
            var nb = NotebookList.SelectedItem as Notebook;
            if (nb != null && nb != currentNotebook) await OpenNotebookAsync(nb, null);
        }

        private async void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ignoreListEvents) return;
            var page = PageList.SelectedItem as PageRef;
            if (page != null && page != currentPage) await OpenPageAsync(page);
        }

        private async void AddPage_Click(object sender, RoutedEventArgs e)
        {
            if (currentNotebook == null) return;
            await SaveNowAsync();
            var page = await store.CreatePageAsync(currentNotebook);
            await OpenNotebookAsync(currentNotebook, page.FileName);
        }

        private async void AddNotebook_Click(object sender, RoutedEventArgs e)
        {
            var name = await AskTextAsync("New notebook", "Notebook", "Create");
            if (string.IsNullOrEmpty(name)) return;
            await SaveNowAsync();
            var nb = await store.CreateNotebookAsync(name);
            await LoadNotebooksAsync(nb.Name, null);
        }

        private void NotebookList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var nb = (e.OriginalSource as FrameworkElement)?.DataContext as Notebook;
            if (nb == null) return;

            var menu = new MenuFlyout();
            var rename = new MenuFlyoutItem { Text = "Rename" };
            rename.Click += async (s, a) =>
            {
                var name = await AskTextAsync("Rename notebook", nb.Name, "Rename");
                if (string.IsNullOrEmpty(name) || name == nb.Name) return;
                await SaveNowAsync();
                bool wasCurrent = nb == currentNotebook;
                string pageName = currentPage != null ? currentPage.FileName : null;
                currentNotebook = null;
                currentPage = null;
                var newName = await store.RenameNotebookAsync(nb, name);
                await LoadNotebooksAsync(wasCurrent ? newName : (string)ApplicationData.Current.LocalSettings.Values["lastNotebook"], pageName);
            };
            var delete = new MenuFlyoutItem { Text = "Delete notebook" };
            delete.Click += async (s, a) =>
            {
                if (!await ConfirmAsync("Delete \"" + nb.Name + "\"?", "The notebook and all its pages go to the Recycle Bin.", "Delete")) return;
                if (nb == currentNotebook) { dirty = false; currentPage = null; currentNotebook = null; }
                else await SaveNowAsync();
                await store.DeleteNotebookAsync(nb);
                await LoadNotebooksAsync(currentNotebook != null ? currentNotebook.Name : null, null);
            };
            menu.Items.Add(rename);
            menu.Items.Add(delete);
            menu.ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
        }

        private void PageList_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            var page = (e.OriginalSource as FrameworkElement)?.DataContext as PageRef;
            if (page == null) return;

            var menu = new MenuFlyout();
            var delete = new MenuFlyoutItem { Text = "Delete page" };
            delete.Click += async (s, a) =>
            {
                if (!await ConfirmAsync("Delete " + page + "?", "The page goes to the Recycle Bin.", "Delete")) return;
                string keep = null;
                if (page == currentPage) { dirty = false; currentPage = null; }
                else { await SaveNowAsync(); keep = currentPage != null ? currentPage.FileName : null; }
                await store.DeletePageAsync(page);
                await OpenNotebookAsync(currentNotebook, keep);
            };
            menu.Items.Add(delete);
            menu.ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
        }

        private void PaneButton_Click(object sender, RoutedEventArgs e)
        {
            Split.IsPaneOpen = !Split.IsPaneOpen;
        }

        // ---------------------------------------------------------------- saving

        private void MarkDirty()
        {
            dirty = true;
            saveTimer.Stop();
            saveTimer.Start(); // save 1.5 s after the last change
        }

        public async Task SaveNowAsync()
        {
            saveTimer.Stop();
            await saveLock.WaitAsync();
            try
            {
                if (!dirty || currentPage == null) return;
                dirty = false;
                await store.SavePageAsync(currentPage, Strokes);
            }
            catch
            {
                dirty = true; // try again on the next change
            }
            finally
            {
                saveLock.Release();
            }
        }

        // ---------------------------------------------------------------- ink events

        private void Presenter_StrokesCollected(InkPresenter sender, InkStrokesCollectedEventArgs args)
        {
            undo.PushAdded(args.Strokes);
            UpdateSheetSize(allowShrink: false);
            MarkDirty();
        }

        private void Presenter_StrokesErased(InkPresenter sender, InkStrokesErasedEventArgs args)
        {
            undo.PushRemoved(args.Strokes);
            MarkDirty();
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            ClearSelection();
            if (undo.Undo(Strokes)) MarkDirty();
        }

        // ---------------------------------------------------------------- tools

        private void Tool_Click(object sender, RoutedEventArgs e)
        {
            Tool t;
            if (Enum.TryParse((string)((FrameworkElement)sender).Tag, out t)) tool = t;
            ApplyTool();
        }

        private void Color_Click(object sender, RoutedEventArgs e)
        {
            penColor = ParseColor((string)((FrameworkElement)sender).Tag);
            foreach (var b in new[] { ColorBlack, ColorBlue, ColorRed, ColorGreen }) b.IsChecked = b == sender;
            if (tool == Tool.Eraser || tool == Tool.Lasso) tool = Tool.Pen;
            ApplyTool();
        }

        private void Size_Click(object sender, RoutedEventArgs e)
        {
            penSize = double.Parse((string)((FrameworkElement)sender).Tag, System.Globalization.CultureInfo.InvariantCulture);
            foreach (var b in new[] { SizeThin, SizeMedium, SizeThick }) b.IsChecked = b == sender;
            if (tool == Tool.Eraser || tool == Tool.Lasso) tool = Tool.Pen;
            ApplyTool();
        }

        private void ApplyTool()
        {
            PenTool.IsChecked = tool == Tool.Pen;
            HighlighterTool.IsChecked = tool == Tool.Highlighter;
            EraserTool.IsChecked = tool == Tool.Eraser;
            LassoTool.IsChecked = tool == Tool.Lasso;
            ClearSelection();

            var presenter = Ink.InkPresenter;
            switch (tool)
            {
                case Tool.Pen:
                    presenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.Inking;
                    var pen = new InkDrawingAttributes
                    {
                        Color = penColor,
                        Size = new Size(penSize, penSize),
                        PenTip = PenTipShape.Circle,
                        IgnorePressure = false, // pressure makes strokes thicker/thinner
                        FitToCurve = true,
                    };
                    presenter.UpdateDefaultDrawingAttributes(pen);
                    break;

                case Tool.Highlighter:
                    presenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.Inking;
                    var hl = new InkDrawingAttributes
                    {
                        // Black ink makes a poor highlighter, so default to yellow.
                        Color = penColor == ParseColor((string)ColorBlack.Tag) ? Color.FromArgb(255, 255, 230, 0) : penColor,
                        Size = new Size(4 + penSize, 12 + penSize * 3),
                        PenTip = PenTipShape.Rectangle,
                        DrawAsHighlighter = true,
                        IgnorePressure = true,
                        FitToCurve = true,
                    };
                    presenter.UpdateDefaultDrawingAttributes(hl);
                    break;

                case Tool.Eraser:
                    presenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.Erasing;
                    break;

                case Tool.Lasso:
                    // All input goes to the lasso handlers below.
                    presenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.None;
                    break;
            }
        }

        private static Color ParseColor(string hex)
        {
            hex = hex.TrimStart('#');
            byte a = Convert.ToByte(hex.Substring(0, 2), 16);
            byte r = Convert.ToByte(hex.Substring(2, 2), 16);
            byte g = Convert.ToByte(hex.Substring(4, 2), 16);
            byte b = Convert.ToByte(hex.Substring(6, 2), 16);
            return Color.FromArgb(a, r, g, b);
        }

        // ---------------------------------------------------------------- lasso select & move

        private void Lasso_PointerPressed(InkUnprocessedInput sender, PointerEventArgs args)
        {
            var pt = args.CurrentPoint.Position;
            if (!selectionRect.IsEmpty && selectionRect.Contains(pt))
            {
                movingSelection = true;
                lastMovePoint = pt;
                totalMove = new Point(0, 0);
                return;
            }

            ClearSelection();
            lassoPoints = new List<Point> { pt };
            lassoLine = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(255, 0x1E, 0x5B, 0xD8)),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
            };
            lassoLine.Points.Add(pt);
            SelectionLayer.Children.Add(lassoLine);
        }

        private void Lasso_PointerMoved(InkUnprocessedInput sender, PointerEventArgs args)
        {
            var pt = args.CurrentPoint.Position;
            if (movingSelection)
            {
                var d = new Point(pt.X - lastMovePoint.X, pt.Y - lastMovePoint.Y);
                Strokes.MoveSelected(d);
                totalMove.X += d.X;
                totalMove.Y += d.Y;
                selectionRect.X += d.X;
                selectionRect.Y += d.Y;
                lastMovePoint = pt;
                DrawSelectionBox();
            }
            else if (lassoLine != null)
            {
                lassoPoints.Add(pt);
                lassoLine.Points.Add(pt);
            }
        }

        private void Lasso_PointerReleased(InkUnprocessedInput sender, PointerEventArgs args)
        {
            if (movingSelection)
            {
                movingSelection = false;
                if (totalMove.X != 0 || totalMove.Y != 0)
                {
                    undo.PushMoved(SelectedStrokes(), totalMove);
                    UpdateSheetSize(allowShrink: false);
                    MarkDirty();
                }
                return;
            }
            if (lassoLine == null) return;

            lassoPoints.Add(args.CurrentPoint.Position);
            SelectionLayer.Children.Remove(lassoLine);
            lassoLine = null;

            var rect = Strokes.SelectWithPolyLine(lassoPoints);
            if (rect.Width > 0 || rect.Height > 0)
            {
                selectionRect = new Rect(rect.X - 6, rect.Y - 6, rect.Width + 12, rect.Height + 12);
                DrawSelectionBox();
                DeleteButton.IsEnabled = true;
            }
        }

        private void DrawSelectionBox()
        {
            SelectionLayer.Children.Clear();
            if (selectionRect.IsEmpty) return;
            var box = new Rectangle
            {
                Width = selectionRect.Width,
                Height = selectionRect.Height,
                Stroke = new SolidColorBrush(Color.FromArgb(255, 0x1E, 0x5B, 0xD8)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = new SolidColorBrush(Color.FromArgb(18, 0x1E, 0x5B, 0xD8)),
            };
            Canvas.SetLeft(box, selectionRect.X);
            Canvas.SetTop(box, selectionRect.Y);
            SelectionLayer.Children.Add(box);
        }

        private List<InkStroke> SelectedStrokes()
        {
            return Strokes.GetStrokes().Where(s => s.Selected).ToList();
        }

        private void ClearSelection()
        {
            if (selectionRect.IsEmpty && lassoLine == null && SelectionLayer.Children.Count == 0) return;
            foreach (var s in Strokes.GetStrokes()) s.Selected = false;
            selectionRect = Rect.Empty;
            lassoLine = null;
            movingSelection = false;
            SelectionLayer.Children.Clear();
            DeleteButton.IsEnabled = false;
        }

        private void DeleteSelection_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedStrokes();
            if (selected.Count == 0) return;
            undo.PushRemoved(selected);
            Strokes.DeleteSelected();
            ClearSelection();
            MarkDirty();
        }

        // ---------------------------------------------------------------- handwriting to text

        private async void ToText_Click(object sender, RoutedEventArgs e)
        {
            if (Strokes.GetStrokes().Count == 0)
            {
                await ShowMessageAsync("Nothing to convert", "Write something on the page first.");
                return;
            }
            var target = Strokes.GetStrokes().Any(s => s.Selected) ? InkRecognitionTarget.Selected : InkRecognitionTarget.All;

            string text;
            try
            {
                var recognizer = new InkRecognizerContainer(); // runs on the device, no internet needed
                var results = await recognizer.RecognizeAsync(Strokes, target);
                // Put each word on the line it was written on, left to right.
                var words = results
                    .Select(r => new RecognizedWord { Rect = r.BoundingRect, Text = r.GetTextCandidates().FirstOrDefault() ?? "" })
                    .OrderBy(w => w.Rect.Top)
                    .ToList();
                var lines = new List<List<RecognizedWord>>();
                foreach (var w in words)
                {
                    var line = lines.LastOrDefault();
                    double mid = w.Rect.Top + w.Rect.Height / 2;
                    if (line != null && mid < line.Max(x => x.Rect.Bottom)) line.Add(w);
                    else lines.Add(new List<RecognizedWord> { w });
                }
                text = string.Join("\r\n", lines.Select(l => string.Join(" ", l.OrderBy(x => x.Rect.Left).Select(x => x.Text))));
            }
            catch (Exception)
            {
                await ShowMessageAsync("Handwriting recognition isn't available",
                    "Install it in Windows Settings > Time & language > Language & region > your language > Language options > Handwriting.");
                return;
            }

            var box = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160, MaxHeight = 400 };
            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            var dialog = new ContentDialog
            {
                Title = target == InkRecognitionTarget.Selected ? "Selection as text" : "Page as text",
                Content = box,
                PrimaryButtonText = "Copy",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var data = new DataPackage();
                data.SetText(box.Text);
                Clipboard.SetContent(data);
            }
        }

        // ---------------------------------------------------------------- palm rejection while the pen hovers

        private sealed class RecognizedWord
        {
            public Rect Rect;
            public string Text;
        }

        private void PenHover_Entering(CoreInkIndependentInputSource sender, PointerEventArgs args)
        {
            if (args.CurrentPoint.PointerDevice.PointerDeviceType != PointerDeviceType.Pen) return;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => SetTouchScrolling(false));
        }

        private void PenHover_Exiting(CoreInkIndependentInputSource sender, PointerEventArgs args)
        {
            if (args.CurrentPoint.PointerDevice.PointerDeviceType != PointerDeviceType.Pen) return;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { penLeftTimer.Stop(); penLeftTimer.Start(); });
        }

        private void SetTouchScrolling(bool enabled)
        {
            if (!enabled) penLeftTimer.Stop();
            var mode = enabled ? ScrollMode.Auto : ScrollMode.Disabled;
            if (Scroller.VerticalScrollMode == mode) return;
            Scroller.VerticalScrollMode = mode;
            Scroller.HorizontalScrollMode = mode;
            Scroller.ZoomMode = enabled ? ZoomMode.Enabled : ZoomMode.Disabled;
        }

        // ---------------------------------------------------------------- page size & ruled lines

        private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSheetSize(allowShrink: false);
        }

        private void UpdateSheetSize(bool allowShrink)
        {
            var b = Strokes.BoundingRect;
            double right = IsReal(b.Right) ? b.Right : 0;
            double bottom = IsReal(b.Bottom) ? b.Bottom : 0;

            double viewW = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Scroller.ActualWidth;
            double viewH = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Scroller.ActualHeight;

            double w = Math.Max(viewW, right + 200);
            // Tall enough to fill the window even fully zoomed out, and always room to keep writing below.
            double h = Math.Max(viewH / Scroller.MinZoomFactor + LineSpacing, bottom + 1200);
            h = Math.Ceiling(h / LineSpacing) * LineSpacing;

            if (!allowShrink && !double.IsNaN(Sheet.Height)) h = Math.Max(h, Sheet.Height);
            if (!allowShrink && !double.IsNaN(Sheet.Width)) w = Math.Max(w, Sheet.Width);
            if (w <= 0 || h <= 0) return;
            if (Sheet.Width == w && Sheet.Height == h) return;

            Sheet.Width = w;
            Sheet.Height = h;

            var lines = new GeometryGroup();
            for (double y = LineSpacing * 2; y < h; y += LineSpacing)
            {
                lines.Children.Add(new LineGeometry { StartPoint = new Point(0, y), EndPoint = new Point(w, y) });
            }
            RuledLines.Data = lines;
        }

        private static bool IsReal(double d) { return !double.IsNaN(d) && !double.IsInfinity(d); }

        // ---------------------------------------------------------------- about & updates

        private async void About_Click(object sender, RoutedEventArgs e)
        {
            Split.IsPaneOpen = false;
            var v = Package.Current.Id.Version;
            var status = new TextBlock { Text = "Checking for updates...", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Colors.Gray) };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = string.Format("Version {0}.{1}.{2}", v.Major, v.Minor, v.Build), FontSize = 18 });
            panel.Children.Add(status);
            var dialog = new ContentDialog
            {
                Title = "Surface Notes",
                Content = panel,
                PrimaryButtonText = "Update now",
                IsPrimaryButtonEnabled = false,
                CloseButtonText = "Close",
            };
            dialog.PrimaryButtonClick += async (d, args) =>
            {
                args.Cancel = true; // keep the dialog open to show progress
                d.IsPrimaryButtonEnabled = false;
                status.Text = "Saving your notes and installing the update. Surface Notes will close when it's done. Open it again from the Start menu.";
                await SaveNowAsync();
                string error = await InstallUpdateAsync();
                if (error != null)
                {
                    status.Text = error;
                    d.IsPrimaryButtonEnabled = true;
                }
            };

            var showing = dialog.ShowAsync();
            await CheckForUpdateAsync(status, dialog);
            await showing;
        }

        private static async Task CheckForUpdateAsync(TextBlock status, ContentDialog dialog)
        {
            const string couldNotCheck = "Couldn't check for updates right now. Make sure you're online and try again.";
            try
            {
                if (Package.Current.SignatureKind == PackageSignatureKind.Developer || Package.Current.GetAppInstallerInfo() == null)
                {
                    status.Text = "This copy wasn't installed from the download page, so it can't update itself.";
                    return;
                }
                var result = await Package.Current.CheckUpdateAvailabilityAsync();
                switch (result.Availability)
                {
                    case PackageUpdateAvailability.NoUpdates:
                        status.Text = "You have the latest version.";
                        break;
                    case PackageUpdateAvailability.Available:
                    case PackageUpdateAvailability.Required:
                        status.Text = "A new version is available.";
                        dialog.IsPrimaryButtonEnabled = true;
                        break;
                    default:
                        status.Text = couldNotCheck;
                        break;
                }
            }
            catch
            {
                status.Text = couldNotCheck;
            }
        }

        /// <summary>Installs the newest version from the download page. Returns an error message, or null on success.</summary>
        private static async Task<string> InstallUpdateAsync()
        {
            try
            {
                var info = Package.Current.GetAppInstallerInfo();
                if (info == null) return "This copy wasn't installed from the download page, so it can't update itself.";
                var pm = new PackageManager();
                // The app is closed while the new version installs.
                var result = await pm.AddPackageByAppInstallerFileAsync(
                    info.Uri, AddPackageByAppInstallerOptions.ForceTargetAppShutdown, pm.GetDefaultPackageVolume());
                if (result.ExtendedErrorCode != null) return "The update didn't install: " + result.ErrorText;
                return null;
            }
            catch (Exception ex)
            {
                return "The update didn't install: " + ex.Message;
            }
        }

        // ---------------------------------------------------------------- dialogs

        private static async Task<string> AskTextAsync(string title, string initial, string primary)
        {
            var box = new TextBox { Text = initial };
            box.Loaded += (s, e) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
            var dialog = new ContentDialog
            {
                Title = title,
                Content = box,
                PrimaryButtonText = primary,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary ? box.Text.Trim() : null;
        }

        private static async Task<bool> ConfirmAsync(string title, string message, string primary)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = primary,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private static async Task ShowMessageAsync(string title, string message)
        {
            var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = "OK" };
            await dialog.ShowAsync();
        }
    }
}
