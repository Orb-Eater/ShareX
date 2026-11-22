#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ShareX.ImageEditor.Core.Annotations;
using ShareX.ImageEditor.Presentation.Controls;
using ShareX.ImageEditor.Presentation.ViewModels;
using SkiaSharp;

namespace ShareX.ImageEditor.Presentation.Views
{
    /// <summary>
    /// Automatic canvas expansion: when text or a shape goes past the image edge,
    /// the canvas grows so nothing is cut off (similar to Yandex Screenshots).
    /// </summary>
    public partial class EditorView
    {
        /// <summary>Free space left between the annotation and the new canvas edge.</summary>
        private const float AutoExpandMargin = 16f;

        /// <summary>While typing, grow in steps so the bitmap is not reallocated on every key press.</summary>
        private const int LiveExpandStep = 150;

        private readonly HashSet<Annotation> _pendingAutoExpandAnnotations = new();
        private bool _autoExpandScheduled;
        private bool _suppressScrollResetForAutoExpand;

        private bool IsAutoExpandCanvasEnabled =>
            DataContext is MainViewModel vm && vm.Options != null && vm.Options.AutoExpandCanvas;

        private SKColor AutoExpandCanvasColor
        {
            get
            {
                string? hex = (DataContext as MainViewModel)?.Options?.CanvasFillColorHex;
                return !string.IsNullOrWhiteSpace(hex) && SKColor.TryParse(hex, out SKColor color) ? color : SKColors.Transparent;
            }
        }

        /// <summary>
        /// Queue a check for the annotation that was just drawn, moved or resized.
        /// Several requests in the same UI turn are merged into one expansion (one undo step).
        /// </summary>
        internal void ScheduleAutoExpandCanvas(Annotation? annotation)
        {
            if (annotation == null || !IsAutoExpandCanvasEnabled || !Core.Editor.EditorCore.CanAutoExpandFor(annotation))
            {
                return;
            }

            _pendingAutoExpandAnnotations.Add(annotation);

            if (_autoExpandScheduled)
            {
                return;
            }

            _autoExpandScheduled = true;
            Dispatcher.UIThread.Post(RunScheduledAutoExpand, DispatcherPriority.Background);
        }

        private void RunScheduledAutoExpand()
        {
            _autoExpandScheduled = false;
            var targets = _pendingAutoExpandAnnotations.ToList();
            _pendingAutoExpandAnnotations.Clear();

            if (_workspaceDisposed || targets.Count == 0)
            {
                return;
            }

            // Text is still being typed: the live expansion handles it, the final fit happens on commit.
            if (_editorCore.IsLiveCanvasExpansionActive)
            {
                return;
            }

            ExpandCanvasToFit(targets);
        }

        private bool ExpandCanvasToFit(IReadOnlyCollection<Annotation> targets) =>
            ExpandCanvasToFit(targets, null);

        /// <param name="hold">Where the picture was on the screen before a live expansion was dropped. When set, the
        /// picture is kept there (see <see cref="HoldCanvasOnScreen"/>) instead of only following a left/top insert
        /// with the scroll bars, which cannot move a picture that is centred in the view.</param>
        private bool ExpandCanvasToFit(IReadOnlyCollection<Annotation> targets, Point? hold)
        {
            if (!IsAutoExpandCanvasEnabled || targets.Count == 0)
            {
                return false;
            }

            Guid? selectedId = (_selectionController.SelectedShape?.Tag as Annotation)?.Id;
            ScrollViewer? scrollViewer = this.FindControl<ScrollViewer>("CanvasScrollViewer");
            Vector scrollOffset = scrollViewer?.Offset ?? default;

            _suppressScrollResetForAutoExpand = true;

            bool expanded = _editorCore.AutoExpandCanvasToFit(targets, AutoExpandMargin, AutoExpandCanvasColor, out int addedLeft, out int addedTop);

            if (!expanded)
            {
                _suppressScrollResetForAutoExpand = false;
                return false;
            }

            bool shifted = addedLeft > 0 || addedTop > 0;

            if (hold.HasValue)
            {
                // The insert moves the picture right/down inside the canvas: the canvas goes left/up by as much.
                double scale = CanvasScale;
                HoldCanvasOnScreen(new Point(hold.Value.X - addedLeft * scale, hold.Value.Y - addedTop * scale));
            }

            // Runs after the posted ImageChanged / AnnotationsRestored handlers.
            Dispatcher.UIThread.Post(() =>
            {
                _suppressScrollResetForAutoExpand = false;

                if (_workspaceDisposed)
                {
                    return;
                }

                if (shifted)
                {
                    // Annotation controls were rebuilt: restore the selection.
                    if (selectedId.HasValue)
                    {
                        ReselectAnnotation(selectedId.Value);
                    }

                    // Content moved right/down: scroll by the same amount so the view does not jump.
                    if (scrollViewer != null && !hold.HasValue)
                    {
                        double zoom = DataContext is MainViewModel vm && vm.Zoom > 0 ? vm.Zoom : 1.0;
                        scrollViewer.Offset = new Vector(scrollOffset.X + addedLeft * zoom, scrollOffset.Y + addedTop * zoom);
                    }
                }
                else
                {
                    _selectionController.UpdateSelectionHandles();
                }
            }, DispatcherPriority.Background);

            return true;
        }

        private void ReselectAnnotation(Guid id)
        {
            var canvas = this.FindControl<Canvas>("AnnotationCanvas");
            if (canvas == null) return;

            foreach (var child in canvas.Children)
            {
                if (child is Control control && control.Tag is Annotation annotation && annotation.Id == id)
                {
                    _selectionController.SetSelectedShape(control);
                    return;
                }
            }
        }

        /// <summary>
        /// A centred or right-aligned text box that grows from its middle or its right edge is kept inside the image
        /// while it fits (TextAnnotation.ClampAnchoredLeft). The box reads the width once, on its first measure.
        /// </summary>
        internal void KeepInsideImage(TextBox textBox)
        {
            (textBox as TextAnnotationEditorBox)?.KeepInsideImage(() => _editorCore.CanvasSize.Width);
        }

        /// <summary>
        /// Called while a new text box is being typed. Grows the canvas right/down so the text stays visible.
        /// Not recorded in history; <see cref="FinishLiveTextExpansion"/> replaces it with one undoable step.
        /// </summary>
        internal void OnLiveTextEditorBoundsChanged(TextBox textBox)
        {
            if (!IsAutoExpandCanvasEnabled || _editorCore.SourceImage == null || textBox.Parent == null)
            {
                return;
            }

            double left = Canvas.GetLeft(textBox);
            double top = Canvas.GetTop(textBox);
            if (double.IsNaN(left) || double.IsNaN(top))
            {
                return;
            }

            double right = left + textBox.Bounds.Width;
            double bottom = top + textBox.Bounds.Height;
            float width = _editorCore.CanvasSize.Width;
            float height = _editorCore.CanvasSize.Height;

            // The tolerance of the final fit (AutoExpandCanvasToFit). Without it a box that ends on the image edge,
            // over it only by a rounding error (a click between two pixels is stored as a float), grew the canvas by
            // a whole step here, and the final fit took that away again when the text was finished.
            // A click outside is already part of the box's bounded anchor. Only new growth beyond that
            // anchor warrants a temporary step; otherwise Enter removes a needless 150-pixel expansion.
            const double tolerance = Core.Editor.EditorCore.AutoExpandTolerance;
            var limits = TextAnnotationEditorBox.LimitsOf(textBox);
            double rightLimit = limits.Limited ? Math.Max(width, limits.Right) : width;
            int addRight = right > rightLimit + tolerance ? (int)Math.Ceiling(right - width) + LiveExpandStep : 0;
            int addBottom = bottom > height + tolerance ? (int)Math.Ceiling(bottom - height) + (int)AutoExpandMargin : 0;

            if (addRight == 0 && addBottom == 0)
            {
                return;
            }

            Point? hold = CanvasOnScreen();
            _suppressScrollResetForAutoExpand = true;
            bool grown = _editorCore.ExpandCanvasLive(addRight, addBottom, AutoExpandCanvasColor);
            if (grown && hold.HasValue)
            {
                HoldCanvasOnScreen(hold.Value);
            }
            Dispatcher.UIThread.Post(() => _suppressScrollResetForAutoExpand = false, DispatcherPriority.Background);
        }

        /// <summary>
        /// Called when typing of a new text ends. Drops the temporary expansion and,
        /// if the text was kept, grows the canvas to fit it exactly as one undoable step.
        /// </summary>
        internal void FinishLiveTextExpansion(Annotation? committedAnnotation)
        {
            // Taken before anything is laid out again: where the picture is on the screen while typing.
            Point? hold = _editorCore.IsLiveCanvasExpansionActive ? CanvasOnScreen() : null;
            bool hadLiveExpansion = _editorCore.EndLiveCanvasExpansion(notify: false);

            bool expanded = committedAnnotation != null && ExpandCanvasToFit(new[] { committedAnnotation }, hold);

            if (!expanded && hadLiveExpansion)
            {
                _suppressScrollResetForAutoExpand = true;
                _editorCore.NotifyImageReplaced();
                if (hold.HasValue)
                {
                    HoldCanvasOnScreen(hold.Value);
                }
                Dispatcher.UIThread.Post(() => _suppressScrollResetForAutoExpand = false, DispatcherPriority.Background);
            }
        }

        // ---------------------------------------------------------------- keeping the picture still on live growth
        // The picture is centred in the view while it is smaller than it, so a canvas that grows by the live step
        // moved the picture, and the text being typed, by half the step in one key press (75 px). A temporary
        // margin on the zoom host (or the scroll position, once the picture is wider than the view) keeps it where
        // it was. It stays after Enter, so the finished text does not move either, and goes on a zoom change, a view
        // that changes size, or an image change that resets the view.

        private LayoutTransformControl? CanvasZoomHost =>
            this.FindControl<ScrollViewer>("CanvasScrollViewer")?.Content as LayoutTransformControl;

        /// <summary>The zoom the canvas is drawn at (the layout transform's own scale).</summary>
        private double CanvasScale =>
            CanvasZoomHost?.LayoutTransform is ScaleTransform scale && scale.ScaleX > 0 ? scale.ScaleX : 1.0;

        /// <summary>
        /// The top left corner of the zoom host, as it was last laid out, in the view at no scroll: its place in the view
        /// plus the scroll offset. <see cref="HoldCanvasOnScreen"/> takes off the offset of its own moment, so a scroll
        /// in between (the view following the caret on the same key) is kept, not undone.
        /// </summary>
        private Point? CanvasOnScreen()
        {
            ScrollViewer? scrollViewer = this.FindControl<ScrollViewer>("CanvasScrollViewer");
            LayoutTransformControl? host = CanvasZoomHost;
            if (scrollViewer == null || host == null || host.Bounds.Width <= 0 || host.Bounds.Height <= 0)
            {
                return null;
            }

            // The scroll presenter arranges its content at minus the scroll offset: the bounds are in the view.
            return host.Bounds.Position + scrollViewer.Offset;
        }

        /// <summary>Set by <see cref="HoldCanvasOnScreen"/>, used by <see cref="ApplyPendingCanvasHold"/>.</summary>
        private Point? _pendingCanvasHold;

        /// <summary>
        /// Called right after the canvas was given a new size in the core (whose ImageChanged event has already queued
        /// its handler): the zoom host's top left corner goes back to <paramref name="unscrolled"/> (from
        /// <see cref="CanvasOnScreen"/>) in that same handler, so no frame shows the new size in the old place.
        /// </summary>
        private void HoldCanvasOnScreen(Point unscrolled) => _pendingCanvasHold = unscrolled;

        /// <summary>
        /// In the ImageChanged handler, after the view model has the new canvas size: lays out once at that size, then
        /// sets the margin and scroll offset that put the zoom host back where it was, less the scroll offset of this
        /// moment (a scroll since the capture is kept). The same job, so a render cannot fall in between.
        /// </summary>
        private void ApplyPendingCanvasHold()
        {
            if (_pendingCanvasHold is not Point unscrolled)
            {
                return;
            }

            _pendingCanvasHold = null;
            ScrollViewer? scrollViewer = this.FindControl<ScrollViewer>("CanvasScrollViewer");
            LayoutTransformControl? host = CanvasZoomHost;
            if (_workspaceDisposed || scrollViewer == null || host == null)
            {
                return;
            }

            Point target = unscrolled - scrollViewer.Offset; // before the layout below can clamp the offset
            host.UpdateLayout(); // the new canvas size
            (double left, double offsetX) = HoldAlong(target.X, host.Bounds.Width, scrollViewer.Viewport.Width);
            (double top, double offsetY) = HoldAlong(target.Y, host.Bounds.Height, scrollViewer.Viewport.Height);
            Thickness margin = new(left, top, 0, 0);
            if (host.Margin != margin)
            {
                host.Margin = margin;
            }

            Vector offset = new(offsetX, offsetY);
            if (scrollViewer.Offset != offset)
            {
                scrollViewer.Offset = offset;
            }
        }

        /// <summary>
        /// One axis: the leading margin and scroll offset that put content of <paramref name="size"/> at
        /// <paramref name="target"/> in a view of <paramref name="viewport"/>. Content (with its margin) that fits is
        /// centred, so there the margin counts half; wider content starts at its margin minus the scroll offset.
        /// </summary>
        private static (double Margin, double Offset) HoldAlong(double target, double size, double viewport)
        {
            if (!double.IsFinite(target) || size <= 0 || viewport <= 0)
            {
                return (0, 0);
            }

            if (target >= 0 && target + size <= viewport)
            {
                return (Math.Max(0, 2 * target - viewport + size), 0);
            }

            if (target >= 0)
            {
                return (target, 0);
            }

            return (0, Math.Min(-target, Math.Max(0, size - viewport)));
        }

        /// <summary>Drops the margin <see cref="HoldCanvasOnScreen"/> left, so the picture is centred again.</summary>
        private void ReleaseCanvasHold()
        {
            _pendingCanvasHold = null;
            if (CanvasZoomHost is LayoutTransformControl host && host.Margin != default)
            {
                host.Margin = default;
            }
        }

        /// <summary>
        /// A view that changes size (the window, a side panel, in the host too) centres the picture again: the hold's
        /// margin was worked out for the old view size, so it would leave the picture off its place.
        /// </summary>
        private void HookCanvasHoldRelease()
        {
            if (this.FindControl<ScrollViewer>("CanvasScrollViewer") is ScrollViewer scrollViewer)
            {
                scrollViewer.SizeChanged += (_, _) => ReleaseCanvasHold();
            }
        }

        /// <summary>
        /// For a zoom: while a hold's margin is in place, the zoom host's top left corner in
        /// <paramref name="scrollViewer"/> as it is now, and the margin is dropped. Null (nothing dropped) without one.
        /// The zoom measures its anchor from there, as the margin it ignores moved the picture.
        /// </summary>
        internal Point? TakeCanvasHold(ScrollViewer scrollViewer)
        {
            _pendingCanvasHold = null;
            if (CanvasZoomHost is not LayoutTransformControl host || host.Margin == default)
            {
                return null;
            }

            Point? place = host.TranslatePoint(default, scrollViewer);
            host.Margin = default;
            return place;
        }
    }
}
