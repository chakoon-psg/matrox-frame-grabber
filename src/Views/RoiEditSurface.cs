using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MatroxFrameGrabber.Infrastructure;
using MatroxFrameGrabber.Mil;

namespace MatroxFrameGrabber.Views
{
    /// <summary>
    /// Draws the analysis rectangle over a live view and lets the operator edit it by dragging.
    ///
    /// Two surfaces show the same rectangle — a pane and the fullscreen overlay — and both must
    /// behave identically, so the wiring lives here once and each host owns an instance. The rules
    /// it applies are in <see cref="RoiGesture"/>; this class only turns mouse positions into image
    /// pixels, shows a preview, and commits on release.
    ///
    /// The visuals are built in code rather than declared twice in XAML, for the same reason.
    /// </summary>
    public sealed class RoiEditSurface
    {
        /// <summary>How close to a handle counts as grabbing it, in control pixels — so the target
        /// stays the same size on screen however far the operator has zoomed.</summary>
        private const double GrabRadius = 7;
        private const double HandleSize = 8;

        /// <summary>A press this short in either axis is a missed click, not a new rectangle.</summary>
        private const double MinNewDragPixels = 5;

        private enum DragMode { None, New, Move, Resize }

        private readonly FrameworkElement _surface;
        private readonly Panel _overlay;
        private readonly string _label;
        private readonly Func<CameraChannel> _channel;
        // null means "drawing". The initial value is neither null nor any real reason, so whatever
        // the first evaluation concludes gets recorded — a surface that never manages to draw at
        // all used to be silent, which is exactly the case that needed explaining.
        private string _hiddenReason = "(not evaluated yet)";
        private bool _offSurface;
        private readonly Rectangle _rect;
        private readonly Rectangle _newRect;
        private readonly Rectangle[] _handles = new Rectangle[8];

        private DragMode _mode;
        private RoiHandle _grabbed;
        private ChannelRoi _dragStartRoi;
        private Point _pressControl;        // where the press landed, in control pixels
        private double _grabImageX, _grabImageY;
        private ChannelRoi _candidate;      // what a release would commit

        public RoiEditSurface(FrameworkElement surface, Panel overlay, Func<CameraChannel> channel,
                              string label)
        {
            _surface = surface;
            _overlay = overlay;
            _channel = channel;
            _label = label;

            Brush roiBrush = FindBrush("Ch1Brush", Brushes.DeepSkyBlue);
            Brush newBrush = FindBrush("AccentBrush", Brushes.Orange);

            _rect = new Rectangle
            {
                Stroke = roiBrush,
                StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _newRect = new Rectangle
            {
                Stroke = newBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            overlay.Children.Add(_rect);
            overlay.Children.Add(_newRect);

            for (int i = 0; i < _handles.Length; i++)
            {
                // Hit testing is done in code against the rectangle's geometry, not by these
                // elements, so that one place decides what the pointer is over.
                _handles[i] = new Rectangle
                {
                    Width = HandleSize,
                    Height = HandleSize,
                    Fill = roiBrush,
                    Stroke = Brushes.White,
                    StrokeThickness = 1,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };
                overlay.Children.Add(_handles[i]);
            }
        }

        /// <summary>Whether drags edit the rectangle. Handles only show while this is on.</summary>
        public bool EditMode { get; set; }

        /// <summary>True between a press and its release, so a host can tell ESC what to cancel.</summary>
        public bool IsDragging => _mode != DragMode.None;

        /// <summary>
        /// Takes the press if edit mode is on. Returns false when the host should handle it —
        /// a double-click to enter or leave fullscreen, for instance.
        /// </summary>
        public bool TryBeginDrag(MouseButtonEventArgs e)
        {
            if (!EditMode || !TryMap(out DisplayMapping map, out ChannelRoi roi, out int fw, out int fh))
                return false;

            _pressControl = e.GetPosition(_surface);
            double ix = map.ToImageX(_pressControl.X), iy = map.ToImageY(_pressControl.Y);
            _dragStartRoi = roi;
            _candidate = roi;

            _grabbed = RoiGesture.HitTest(roi, ix, iy, GrabRadius / map.Scale);
            if (RoiGesture.IsResize(_grabbed))
            {
                _mode = DragMode.Resize;
            }
            else if (_grabbed == RoiHandle.Inside)
            {
                _mode = DragMode.Move;
                _grabImageX = ix;
                _grabImageY = iy;
            }
            else
            {
                // Outside the rectangle (or there isn't one yet): rubber-band a new one.
                _mode = DragMode.New;
                _newRect.Margin = new Thickness(_pressControl.X, _pressControl.Y, 0, 0);
                _newRect.Width = 0;
                _newRect.Height = 0;
                _newRect.Visibility = Visibility.Visible;
            }

            _surface.CaptureMouse();
            return true;
        }

        /// <summary>Updates the preview, or the cursor when no drag is in progress.</summary>
        public void ContinueDrag(MouseEventArgs e)
        {
            if (!EditMode)
                return;
            if (_mode == DragMode.None)
            {
                UpdateCursor(e);
                return;
            }
            if (!TryMap(out DisplayMapping map, out _, out int fw, out int fh))
                return;

            Point now = e.GetPosition(_surface);

            if (_mode == DragMode.New)
            {
                _newRect.Margin = new Thickness(Math.Min(_pressControl.X, now.X),
                                                Math.Min(_pressControl.Y, now.Y), 0, 0);
                _newRect.Width = Math.Abs(now.X - _pressControl.X);
                _newRect.Height = Math.Abs(now.Y - _pressControl.Y);
                return;
            }

            double ix = map.ToImageX(now.X), iy = map.ToImageY(now.Y);
            _candidate = _mode == DragMode.Resize
                ? RoiGesture.Resize(_dragStartRoi, _grabbed, ix, iy, fw, fh)
                : RoiGesture.Move(_dragStartRoi,
                                  (int)Math.Round(ix - _grabImageX),
                                  (int)Math.Round(iy - _grabImageY), fw, fh);
            Draw(_candidate, map);
        }

        /// <summary>Commits what the preview showed.</summary>
        public void EndDrag(MouseButtonEventArgs e)
        {
            if (_mode == DragMode.None)
                return;

            DragMode mode = _mode;
            _mode = DragMode.None;
            _surface.ReleaseMouseCapture();
            _newRect.Visibility = Visibility.Collapsed;

            CameraChannel channel = _channel();
            if (channel == null || !TryMap(out DisplayMapping map, out _, out _, out _))
                return;

            if (mode == DragMode.New)
            {
                Point end = e.GetPosition(_surface);
                if (Math.Abs(end.X - _pressControl.X) < MinNewDragPixels ||
                    Math.Abs(end.Y - _pressControl.Y) < MinNewDragPixels)
                {
                    Refresh();     // a missed click leaves the old rectangle alone
                    return;
                }
                int x0 = (int)Math.Round(map.ToImageX(Math.Min(_pressControl.X, end.X)));
                int y0 = (int)Math.Round(map.ToImageY(Math.Min(_pressControl.Y, end.Y)));
                int x1 = (int)Math.Round(map.ToImageX(Math.Max(_pressControl.X, end.X)));
                int y1 = (int)Math.Round(map.ToImageY(Math.Max(_pressControl.Y, end.Y)));
                channel.SetAnalysisRoiFromDrag(x0, y0, x1 - x0, y1 - y0);
            }
            else
            {
                channel.SetAnalysisRoiFromDrag(_candidate.OffsetX, _candidate.OffsetY,
                                               _candidate.Width, _candidate.Height);
            }

            Refresh();
        }

        /// <summary>Abandons a drag in progress, leaving the stored rectangle untouched.</summary>
        public void CancelDrag()
        {
            if (_mode == DragMode.None)
                return;
            _mode = DragMode.None;
            _surface.ReleaseMouseCapture();
            _newRect.Visibility = Visibility.Collapsed;
            Refresh();
        }

        /// <summary>
        /// Repositions the rectangle and its handles from the channel's stored ROI. Driven by the
        /// window's 500 ms tick: MIL owns zoom and pan natively and raises no event to follow.
        /// </summary>
        public void Refresh()
        {
            if (_mode != DragMode.None)
                return;     // a drag is showing its own candidate; don't fight it
            if (!TryMap(out DisplayMapping map, out ChannelRoi roi, out _, out _, out string reason))
            {
                // Logged on the transition only, never per tick: a rectangle that stops drawing is
                // invisible by definition, so the reason has to be recorded when it happens.
                if (_hiddenReason != reason)
                {
                    MilErrorLog.Note($"{Who()} ROI rectangle not drawn - {reason}");
                    _hiddenReason = reason;
                }
                Hide();
                return;
            }
            // No rectangle to draw is not the same as failing to draw one, and only the second is
            // worth a log line.
            if (roi.IsFullFrame)
            {
                if (_hiddenReason != "full frame")
                {
                    MilErrorLog.Note($"{Who()} ROI rectangle not drawn - full frame, nothing to draw");
                    _hiddenReason = "full frame";
                }
                Hide();
                return;
            }
            if (_hiddenReason != null)
            {
                MilErrorLog.Note($"{Who()} ROI rectangle drawing");
                _hiddenReason = null;
            }
            Draw(roi, map);
        }

        /// <summary>Shows what the press would do, before the operator commits to it.</summary>
        public void UpdateCursor(MouseEventArgs e)
        {
            if (!EditMode)
            {
                _surface.Cursor = null;
                return;
            }
            if (!TryMap(out DisplayMapping map, out ChannelRoi roi, out _, out _))
            {
                _surface.Cursor = Cursors.Cross;
                return;
            }

            Point p = e.GetPosition(_surface);
            switch (RoiGesture.HitTest(roi, map.ToImageX(p.X), map.ToImageY(p.Y), GrabRadius / map.Scale))
            {
                case RoiHandle.TopLeft:
                case RoiHandle.BottomRight: _surface.Cursor = Cursors.SizeNWSE; break;
                case RoiHandle.TopRight:
                case RoiHandle.BottomLeft: _surface.Cursor = Cursors.SizeNESW; break;
                case RoiHandle.Left:
                case RoiHandle.Right: _surface.Cursor = Cursors.SizeWE; break;
                case RoiHandle.Top:
                case RoiHandle.Bottom: _surface.Cursor = Cursors.SizeNS; break;
                case RoiHandle.Inside: _surface.Cursor = Cursors.SizeAll; break;
                default: _surface.Cursor = Cursors.Cross; break;
            }
        }

        /// <summary>
        /// Takes this surface's visuals back out of the overlay it was given.
        ///
        /// The fullscreen overlay builds a surface per entry, and the visuals belong to the
        /// surface rather than to the panel — leaving them behind stacked a frozen rectangle and
        /// eight frozen handles on every entry, which reads exactly like the rectangle no longer
        /// being tracked. Idempotent.
        /// </summary>
        public void RemoveVisuals()
        {
            ExitEditMode();
            _overlay.Children.Remove(_rect);
            _overlay.Children.Remove(_newRect);
            foreach (Rectangle handle in _handles)
                _overlay.Children.Remove(handle);
        }

        /// <summary>Clears the cursor and hides the handles — call when leaving edit mode.</summary>
        public void ExitEditMode()
        {
            EditMode = false;
            CancelDrag();
            _surface.Cursor = null;
            Refresh();
        }

        // ----- Drawing -----

        private void Draw(ChannelRoi roi, DisplayMapping map)
        {
            if (roi.IsFullFrame)
            {
                Hide();
                return;
            }

            double left = map.ToControlX(roi.OffsetX);
            double top = map.ToControlY(roi.OffsetY);
            double w = roi.Width * map.Scale;
            double h = roi.Height * map.Scale;

            _rect.Margin = new Thickness(left, top, 0, 0);
            _rect.Width = w;
            _rect.Height = h;
            _rect.Visibility = Visibility.Visible;

            // Drawn is not the same as visible. If the mapping is built from a zoom that belongs to
            // a different control — the display is shared with the fullscreen overlay — the
            // rectangle lands outside this surface and the operator sees nothing, with no failure
            // anywhere to notice.
            double sw = _surface.ActualWidth, sh = _surface.ActualHeight;
            bool offSurface = left + w <= 0 || top + h <= 0 || left >= sw || top >= sh;
            if (offSurface != _offSurface)
            {
                _offSurface = offSurface;
                if (offSurface)
                    MilErrorLog.Note($"{Who()} ROI drawn off the surface - rect {left:F0},{top:F0} "
                                   + $"{w:F0}x{h:F0} in surface {sw:F0}x{sh:F0}, scale {map.Scale:F3}");
                else
                    MilErrorLog.Note($"{Who()} ROI back on the surface");
            }

            if (!EditMode)
            {
                foreach (Rectangle handle in _handles)
                    handle.Visibility = Visibility.Collapsed;
                return;
            }

            double midX = left + w / 2, midY = top + h / 2, right = left + w, bottom = top + h;
            PlaceHandle(0, left, top); PlaceHandle(1, midX, top); PlaceHandle(2, right, top);
            PlaceHandle(3, left, midY); PlaceHandle(4, right, midY);
            PlaceHandle(5, left, bottom); PlaceHandle(6, midX, bottom); PlaceHandle(7, right, bottom);
        }

        private void PlaceHandle(int index, double centreX, double centreY)
        {
            Rectangle handle = _handles[index];
            handle.Margin = new Thickness(centreX - HandleSize / 2, centreY - HandleSize / 2, 0, 0);
            handle.Visibility = Visibility.Visible;
        }

        private void Hide()
        {
            _rect.Visibility = Visibility.Collapsed;
            foreach (Rectangle handle in _handles)
                handle.Visibility = Visibility.Collapsed;
        }

        private bool TryMap(out DisplayMapping map, out ChannelRoi roi, out int frameW, out int frameH)
            => TryMap(out map, out roi, out frameW, out frameH, out _);

        /// <summary>
        /// The mapping, and when it cannot be built, why. The reason is worth carrying: a
        /// rectangle that silently stops drawing looks identical whether the control has no size,
        /// MIL will not report a zoom, or the channel is gone — and this app has now lost the
        /// rectangle twice for reasons that were only distinguishable from the inside.
        /// </summary>
        private bool TryMap(out DisplayMapping map, out ChannelRoi roi, out int frameW, out int frameH,
                            out string reason)
        {
            map = default(DisplayMapping);
            roi = ChannelRoi.FullFrame;
            frameW = 0; frameH = 0;
            reason = null;

            CameraChannel channel = _channel();
            if (channel == null)
            {
                reason = "no channel bound";
                return false;
            }
            if (!channel.TryGetViewGeometry(out frameW, out frameH, out double zoom, out double ox, out double oy))
            {
                reason = "MIL would not report the view geometry";
                return false;
            }

            map = DisplayMapping.Create(_surface.ActualWidth, _surface.ActualHeight,
                                        frameW, frameH, zoom, ox, oy);
            roi = channel.AnalysisRoi;
            if (!map.IsValid)
            {
                reason = $"mapping invalid: surface {_surface.ActualWidth:F0}x{_surface.ActualHeight:F0}, "
                       + $"frame {frameW}x{frameH}, zoom {zoom:F3}";
                return false;
            }
            return true;
        }

        /// <summary>Which surface this is, for the log — four panes and an overlay share the code.</summary>
        private string Who() => $"{_channel()?.Name ?? "no channel"} ({_label}):";

        private static Brush FindBrush(string key, Brush fallback)
        {
            object found = Application.Current?.TryFindResource(key);
            return found as Brush ?? fallback;
        }
    }
}
