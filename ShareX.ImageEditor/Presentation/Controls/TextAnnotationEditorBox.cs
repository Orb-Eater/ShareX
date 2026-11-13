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
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using ShareX.ImageEditor.Core.Annotations;

namespace ShareX.ImageEditor.Presentation.Controls
{
    /// <summary>
    /// The text box a text annotation is typed or edited in. It shows the finished look while typing: it draws the
    /// box, the outline and the letters itself, with OutlinedTextControl's own drawing, underneath a text box whose
    /// letters are invisible. Only the caret, the selection and the thin marker border come from the text box.
    /// A plain TextBox cannot do this: it has no outline for letters, and the app theme styles its inner parts
    /// directly, so it always showed the theme's dark box and text colour whatever the annotation looked like.
    /// </summary>
    internal sealed class TextAnnotationEditorBox : TextBox
    {
        private readonly TextAnnotation _annotation;
        private readonly IBrush _textBrush;
        private TextPresenter? _presenter;
        private ScrollViewer? _scrollViewer;
        private bool _composing;

        // Styled and templated as an ordinary text box.
        protected override Type StyleKeyOverride => typeof(TextBox);

        public TextAnnotationEditorBox(TextAnnotation annotation, IBrush textBrush)
        {
            _annotation = annotation;
            _textBrush = textBrush;

            Foreground = textBrush;
            Background = Brushes.Transparent;
            // The theme clips a text box to its bounds, which cut the drop shadow off at the box's edge.
            ClipToBounds = false;
            // The caret has to show against what is behind the letters: the box when there is one, else the page.
            // On the page the letters themselves may not show (white on a light screenshot); their outline does.
            CaretBrush = !annotation.HasBox && annotation.HasVisibleOutline && Color.TryParse(annotation.StrokeColor, out Color outline)
                ? new SolidColorBrush(outline)
                : textBrush;
            // The selected letters are the drawn ones underneath, seen through the selection.
            SelectionBrush = new SolidColorBrush(Color.FromArgb(110, 62, 131, 242));
            SelectionForegroundBrush = Brushes.Transparent;
        }

        // The box grows from the point its text's alignment holds (TextAnnotation.SetSizeAnchored), not from its top
        // left corner, while it has no set width (a new text, or a finished one that was never resized).
        private bool _followAnchor;

        // From the annotation's coordinates to those of the canvas the box is on (the overlay is offset by its bleed).
        private Vector? _parentOffset;

        internal void FollowAnchor() => _followAnchor = true;

        // The box is placed here, in its own measure, from the size it is about to take: the canvas arranges it in
        // the same layout pass, so the box never shows a frame at the old place with the new size.
        protected override Size MeasureOverride(Size availableSize)
        {
            Size size = base.MeasureOverride(availableSize);

            if (_followAnchor && double.IsNaN(Width))
            {
                PlaceAtAnchor(size);
            }

            return size;
        }

        private void PlaceAtAnchor(Size size)
        {
            double left = Canvas.GetLeft(this), top = Canvas.GetTop(this);
            if (double.IsNaN(left) || double.IsNaN(top))
            {
                return;
            }

            // The annotation keeps the rectangle the text had when the box was opened (a new text: its click point)
            // until the text is finished, and is then fitted from that same rectangle, to the same place.
            SkiaSharp.SKRect from = _annotation.GetStoredBounds();
            _parentOffset ??= new Vector(left - from.Left, top - from.Top);

            if (UseLayoutRounding)
            {
                size = Avalonia.Layout.LayoutHelper.RoundLayoutSizeUp(size, Avalonia.Layout.LayoutHelper.GetLayoutScale(this));
            }

            Point topLeft = TextAnnotation.GetAnchoredTopLeft(from, size.Width, size.Height, _annotation.HorizontalAlignment, _annotation.RotationAngle)
                + _parentOffset.Value;

            if (Math.Abs(topLeft.X - left) > 0.001)
            {
                Canvas.SetLeft(this, topLeft.X);
            }

            if (Math.Abs(topLeft.Y - top) > 0.001)
            {
                Canvas.SetTop(this, topLeft.Y);
            }
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            // Values set on the parts themselves win over the theme, in every state (focused, hovered).
            if (e.NameScope.Find<Border>("PART_BorderElement") is { } border)
            {
                border.Background = Brushes.Transparent;
                border.Bind(Border.BorderBrushProperty, this.GetObservable(BorderBrushProperty));
                border.Bind(Border.BorderThicknessProperty, this.GetObservable(BorderThicknessProperty));
                border.Bind(Border.CornerRadiusProperty, this.GetObservable(CornerRadiusProperty));
            }

            if (_presenter != null)
            {
                _presenter.PropertyChanged -= OnPresenterPropertyChanged;
            }

            _presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");

            if (_presenter != null)
            {
                _presenter.Foreground = Brushes.Transparent;
                _presenter.PropertyChanged += OnPresenterPropertyChanged;
            }

            if (_scrollViewer != null)
            {
                _scrollViewer.PropertyChanged -= OnScrollViewerPropertyChanged;
            }

            _scrollViewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");

            if (_scrollViewer != null)
            {
                _scrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
            }
        }

        // Text that does not fit the box is scrolled by the text box to keep the caret in view (when a finished
        // text was made smaller than its text and is edited again). The drawn letters have to move with it.
        private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ScrollViewer.OffsetProperty)
            {
                InvalidateVisual();
            }
        }

        private void OnPresenterPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TextPresenter.PreeditTextProperty && _presenter != null)
            {
                // Text an input method is still composing is not part of Text yet, so it cannot be drawn from here.
                // Until it is committed the text box shows its own letters, as it did before.
                _composing = !string.IsNullOrEmpty(_presenter.PreeditText);
                _presenter.Foreground = _composing ? _textBrush : Brushes.Transparent;
                InvalidateVisual();
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == TextProperty || change.Property == BoundsProperty)
            {
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            Size size = Bounds.Size;

            if (_annotation.HasBox)
            {
                OutlinedTextControl.DrawBox(context, _annotation, size);
            }
            else if (!_annotation.HasVisibleOutline || string.IsNullOrEmpty(Text))
            {
                DrawBackdrop(context, size);
            }

            if (!_composing)
            {
                OutlinedTextControl.DrawText(context, _annotation, Text, size, _scrollViewer?.Offset ?? default);
            }
        }

        // Bare letters (no box, no outline) can have the colour of what is under them, white on a light screenshot
        // for one. This faint backdrop, there only while typing, keeps the letters and the caret readable. It also
        // shows where an empty box without a fill is, before the first letter gives it an outline to show.
        private void DrawBackdrop(DrawingContext context, Size size)
        {
            Color text = (_textBrush as ISolidColorBrush)?.Color ?? Colors.White;
            bool lightText = (0.299 * text.R) + (0.587 * text.G) + (0.114 * text.B) > 140;
            Color backdrop = lightText ? Color.FromArgb(120, 0, 0, 0) : Color.FromArgb(120, 255, 255, 255);
            context.DrawRectangle(new SolidColorBrush(backdrop), null, new Rect(size), 4, 4);
        }
    }
}
