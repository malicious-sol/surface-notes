using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Input.Inking;
using Windows.UI.Input.Inking.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace SurfaceNotes
{
    /// <summary>
    /// The one screen of the app. This file has the ink, tools, lasso and page layout.
    /// MainPage.Notebooks.cs opens and saves notebooks and pages; MainPage.Dialogs.cs has dialogs and the updater.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private enum Tool { Pen, Highlighter, Eraser, Lasso }

        private const double LineSpacing = 40;
        private static readonly Color SelectionBlue = Color.FromArgb(255, 0x1E, 0x5B, 0xD8);

        private readonly UndoStack undo = new UndoStack();

        // While the pen is near the screen, touch can't scroll or zoom, so a resting palm can't move the page.
        // Touch comes back this long after the pen moves away.
        private readonly DispatcherTimer penLeftTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        private CoreInkIndependentInputSource penHover;

        private Tool tool = Tool.Pen;
        private Color penColor = Color.FromArgb(255, 0x11, 0x11, 0x11);
        private double penSize = 3;

        // Lasso state
        private Polyline lassoLine;
        private List<Point> lassoPoints;
        private Rectangle selectionBox;
        private Rect selectionRect = Rect.Empty;
        private bool movingSelection;
        private bool ignoringContact; // a pen-down that only dismissed a selection
        private Point lastMovePoint;
        private Point totalMove;

        // How far the ink reaches, so the sheet only grows when it needs to.
        private double inkRight, inkBottom;
        private readonly SolidColorBrush ruleBrush = new SolidColorBrush(Color.FromArgb(255, 0xE3, 0xE9, 0xF0));

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
            presenter.UnprocessedInput.PointerLost += Lasso_PointerLost;

            // Palm rejection, part 2: while the pen hovers over the page, a resting palm
            // can't scroll or zoom it either.
            try
            {
                penHover = CoreInkIndependentInputSource.Create(presenter);
                penHover.PointerEntering += PenHover_Entering;
                penHover.PointerExiting += PenHover_Exiting;
            }
            catch (Exception ex)
            {
                penHover = null; // Touch still never draws.
                Log.Error("Pen hover", ex);
            }
            penLeftTimer.Tick += (s, e) => { penLeftTimer.Stop(); SetTouchScrolling(true); };

            InitNotebooks();
            InitDialogs();
            LoadPenSettings();
            ApplyTool();

            // In portrait the toolbar is tight, so drop the page title there (the side panel shows it).
            SizeChanged += (s, e) => TitleText.Visibility = e.NewSize.Width >= 1350 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------------------------------------------------------- ink events

        private void Presenter_StrokesCollected(InkPresenter sender, InkStrokesCollectedEventArgs args)
        {
            undo.PushAdded(args.Strokes);
            foreach (var stroke in args.Strokes) GrowInkExtent(stroke.BoundingRect);
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
            if (busy || !pageLoaded) return;
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
            if (tool == Tool.Eraser || tool == Tool.Lasso) tool = Tool.Pen;
            SavePenSettings();
            ApplyTool();
        }

        private void Size_Click(object sender, RoutedEventArgs e)
        {
            penSize = double.Parse((string)((FrameworkElement)sender).Tag, CultureInfo.InvariantCulture);
            if (tool == Tool.Eraser || tool == Tool.Lasso) tool = Tool.Pen;
            SavePenSettings();
            ApplyTool();
        }

        private void LoadPenSettings()
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            var color = settings["penColor"] as string;
            if (color != null && ColorButtons().Any(b => (string)b.Tag == color)) penColor = ParseColor(color);
            var size = settings["penSize"] as double?;
            if (size.HasValue && SizeButtons().Any(b => ParseSize(b) == size.Value)) penSize = size.Value;
        }

        private void SavePenSettings()
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            settings["penColor"] = ColorTag(penColor);
            settings["penSize"] = penSize;
        }

        private ToggleButton[] ColorButtons() { return new[] { ColorBlack, ColorBlue, ColorRed, ColorGreen }; }
        private ToggleButton[] SizeButtons() { return new[] { SizeThin, SizeMedium, SizeThick }; }
        private static double ParseSize(FrameworkElement b) { return double.Parse((string)b.Tag, CultureInfo.InvariantCulture); }

        private void ApplyTool()
        {
            PenTool.IsChecked = tool == Tool.Pen;
            HighlighterTool.IsChecked = tool == Tool.Highlighter;
            EraserTool.IsChecked = tool == Tool.Eraser;
            LassoTool.IsChecked = tool == Tool.Lasso;
            string colorTag = ColorTag(penColor);
            foreach (var b in ColorButtons()) b.IsChecked = (string)b.Tag == colorTag;
            foreach (var b in SizeButtons()) b.IsChecked = ParseSize(b) == penSize;
            ClearSelection();

            var presenter = Ink.InkPresenter;
            if (tool == Tool.Pen)
            {
                presenter.UpdateDefaultDrawingAttributes(new InkDrawingAttributes
                {
                    Color = penColor,
                    Size = new Size(penSize, penSize),
                    PenTip = PenTipShape.Circle,
                    IgnorePressure = false, // pressure makes strokes thicker/thinner
                    FitToCurve = true,
                });
            }
            else if (tool == Tool.Highlighter)
            {
                presenter.UpdateDefaultDrawingAttributes(new InkDrawingAttributes
                {
                    // Black ink makes a poor highlighter, so default to yellow.
                    Color = colorTag == (string)ColorBlack.Tag ? Color.FromArgb(255, 255, 230, 0) : penColor,
                    Size = new Size(4 + penSize, 12 + penSize * 3),
                    PenTip = PenTipShape.Rectangle,
                    DrawAsHighlighter = true,
                    IgnorePressure = true,
                    FitToCurve = true,
                });
            }
            ApplyInputMode();
        }

        /// <summary>What a pen-down on the page does: ink, erase, or (lasso tool, or while something is selected) go to the lasso code.</summary>
        private void ApplyInputMode()
        {
            var config = Ink.InkPresenter.InputProcessingConfiguration;
            if (tool == Tool.Lasso || !selectionRect.IsEmpty) config.Mode = InkInputProcessingMode.None;
            else if (tool == Tool.Eraser) config.Mode = InkInputProcessingMode.Erasing;
            else config.Mode = InkInputProcessingMode.Inking;
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

        private static string ColorTag(Color c)
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", c.A, c.R, c.G, c.B);
        }

        // ---------------------------------------------------------------- lasso select & move

        private void Lasso_PointerPressed(InkUnprocessedInput sender, PointerEventArgs args)
        {
            var pt = args.CurrentPoint.Position;
            ignoringContact = false;
            if (!selectionRect.IsEmpty)
            {
                if (selectionRect.Contains(pt))
                {
                    movingSelection = true;
                    lastMovePoint = pt;
                    totalMove = new Point(0, 0);
                    return;
                }
                // Touching outside the selection lets go of it. With the pen or highlighter, that touch
                // doesn't draw, the same as in other note apps; the side button still starts a new lasso.
                ClearSelection();
                var props = args.CurrentPoint.Properties;
                if (tool != Tool.Lasso && !props.IsBarrelButtonPressed && !props.IsRightButtonPressed)
                {
                    ignoringContact = true;
                    return;
                }
            }

            lassoPoints = new List<Point> { pt };
            lassoLine = new Polyline
            {
                Stroke = new SolidColorBrush(SelectionBlue),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
            };
            lassoLine.Points.Add(pt);
            SelectionLayer.Children.Add(lassoLine);
        }

        private void Lasso_PointerMoved(InkUnprocessedInput sender, PointerEventArgs args)
        {
            if (ignoringContact) return;
            var pt = args.CurrentPoint.Position;
            if (movingSelection)
            {
                double dx = pt.X - lastMovePoint.X;
                double dy = pt.Y - lastMovePoint.Y;
                lastMovePoint = pt;
                // Keep the selection on the page: nothing can scroll above or left of its top corner.
                if (selectionRect.X + dx < 0) dx = -selectionRect.X;
                if (selectionRect.Y + dy < 0) dy = -selectionRect.Y;
                if (dx == 0 && dy == 0) return;
                Strokes.MoveSelected(new Point(dx, dy));
                totalMove.X += dx;
                totalMove.Y += dy;
                selectionRect.X += dx;
                selectionRect.Y += dy;
                PlaceSelectionBox();
            }
            else if (lassoLine != null)
            {
                // Skip points closer than 3 px: the dashed line redraws with every point, so fewer is cheaper.
                var last = lassoPoints[lassoPoints.Count - 1];
                if (Math.Abs(pt.X - last.X) + Math.Abs(pt.Y - last.Y) < 3) return;
                lassoPoints.Add(pt);
                lassoLine.Points.Add(pt);
            }
        }

        private void Lasso_PointerReleased(InkUnprocessedInput sender, PointerEventArgs args)
        {
            if (ignoringContact)
            {
                ignoringContact = false;
                return;
            }
            if (movingSelection)
            {
                movingSelection = false;
                if (totalMove.X != 0 || totalMove.Y != 0)
                {
                    undo.PushMoved(SelectedStrokes(), totalMove);
                    GrowInkExtent(selectionRect);
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
            lassoPoints = null;
            if (rect.Width > 0 || rect.Height > 0)
            {
                selectionRect = new Rect(rect.X - 6, rect.Y - 6, rect.Width + 12, rect.Height + 12);
                PlaceSelectionBox();
                DeleteButton.Visibility = Visibility.Visible;
            }
            ApplyInputMode();
        }

        private void Lasso_PointerLost(InkUnprocessedInput sender, PointerEventArgs args)
        {
            ignoringContact = false;
            if (lassoLine != null)
            {
                SelectionLayer.Children.Remove(lassoLine);
                lassoLine = null;
                lassoPoints = null;
            }
            if (movingSelection)
            {
                movingSelection = false;
                if (totalMove.X != 0 || totalMove.Y != 0)
                {
                    undo.PushMoved(SelectedStrokes(), totalMove);
                    MarkDirty();
                }
            }
        }

        private void PlaceSelectionBox()
        {
            if (selectionBox == null)
            {
                selectionBox = new Rectangle
                {
                    Stroke = new SolidColorBrush(SelectionBlue),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    Fill = new SolidColorBrush(Color.FromArgb(18, 0x1E, 0x5B, 0xD8)),
                };
                SelectionLayer.Children.Add(selectionBox);
            }
            if (selectionBox.Width != selectionRect.Width) selectionBox.Width = selectionRect.Width;
            if (selectionBox.Height != selectionRect.Height) selectionBox.Height = selectionRect.Height;
            Canvas.SetLeft(selectionBox, selectionRect.X);
            Canvas.SetTop(selectionBox, selectionRect.Y);
            selectionBox.Visibility = Visibility.Visible;
        }

        private List<InkStroke> SelectedStrokes()
        {
            return Strokes.GetStrokes().Where(s => s.Selected).ToList();
        }

        private void ClearSelection()
        {
            if (selectionRect.IsEmpty && lassoLine == null && !movingSelection) return;
            foreach (var s in Strokes.GetStrokes()) s.Selected = false;
            selectionRect = Rect.Empty;
            movingSelection = false;
            if (lassoLine != null)
            {
                SelectionLayer.Children.Remove(lassoLine);
                lassoLine = null;
                lassoPoints = null;
            }
            if (selectionBox != null) selectionBox.Visibility = Visibility.Collapsed;
            DeleteButton.Visibility = Visibility.Collapsed;
            ApplyInputMode();
        }

        private void DeleteSelection_Click(object sender, RoutedEventArgs e)
        {
            if (busy) return;
            var selected = SelectedStrokes();
            if (selected.Count == 0) return;
            undo.PushRemoved(selected);
            Strokes.DeleteSelected();
            ClearSelection();
            MarkDirty();
        }

        // ---------------------------------------------------------------- palm rejection while the pen hovers

        private void PenHover_Entering(CoreInkIndependentInputSource sender, PointerEventArgs args)
        {
            if (args.CurrentPoint.PointerDevice.PointerDeviceType != PointerDeviceType.Pen) return;
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.High, () => SetTouchScrolling(false));
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

        private void ResetInkExtent()
        {
            inkRight = 0;
            inkBottom = 0;
            if (Strokes.GetStrokes().Count > 0) GrowInkExtent(Strokes.BoundingRect);
        }

        private void GrowInkExtent(Rect r)
        {
            if (r.IsEmpty) return;
            if (IsReal(r.Right)) inkRight = Math.Max(inkRight, r.Right);
            if (IsReal(r.Bottom)) inkBottom = Math.Max(inkBottom, r.Bottom);
        }

        private void UpdateSheetSize(bool allowShrink)
        {
            double viewW = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Scroller.ActualWidth;
            double viewH = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Scroller.ActualHeight;

            double w = Math.Max(viewW, inkRight + 200);
            // Tall enough to fill the window even fully zoomed out, and always room to keep writing below.
            double h = Math.Max(viewH / Scroller.MinZoomFactor + LineSpacing, inkBottom + 1200);
            h = Math.Ceiling(h / LineSpacing) * LineSpacing;

            if (!allowShrink && !double.IsNaN(Sheet.Height)) h = Math.Max(h, Sheet.Height);
            if (!allowShrink && !double.IsNaN(Sheet.Width)) w = Math.Max(w, Sheet.Width);
            if (w <= 0 || h <= 0) return;

            if (Sheet.Width != w) Sheet.Width = w;
            if (Sheet.Height != h)
            {
                Sheet.Height = h;
                // Each line is a 1 px border at the bottom of a LineSpacing-tall row; the first is at 2 * LineSpacing.
                int count = Math.Max(0, (int)((h - 2 * LineSpacing) / LineSpacing));
                var lines = RuledLines.Children;
                while (lines.Count > count) lines.RemoveAt(lines.Count - 1);
                while (lines.Count < count)
                {
                    lines.Add(new Border { Height = LineSpacing, BorderBrush = ruleBrush, BorderThickness = new Thickness(0, 0, 0, 1) });
                }
            }
        }

        private static bool IsReal(double d) { return !double.IsNaN(d) && !double.IsInfinity(d); }
    }
}
