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
using ShareX.ImageEditor.Core.Annotations;
using ShareX.ImageEditor.Presentation.Helpers;
using System.Globalization;

namespace ShareX.ImageEditor.Presentation.Controls
{
    /// <summary>
    /// Custom control for rendering text with both an outline (stroke) and a fill.
    /// Avalonia's native TextBox does not support text strokes, so we render via FormattedText and Geometry.
    /// </summary>
    public class OutlinedTextControl : Control
    {
        internal const double TextPadding = 8;

        public static readonly StyledProperty<TextAnnotation?> AnnotationProperty =
            AvaloniaProperty.Register<OutlinedTextControl, TextAnnotation?>(nameof(Annotation));

        public TextAnnotation? Annotation
        {
            get => GetValue(AnnotationProperty);
            set => SetValue(AnnotationProperty, value);
        }

        static OutlinedTextControl()
        {
            AffectsRender<OutlinedTextControl>(AnnotationProperty);
            AffectsMeasure<OutlinedTextControl>(AnnotationProperty);
        }

        // No ClipToBounds: the control's drop shadow has to reach outside the box. DrawText clips the letters
        // to the annotation instead, which is all the clip was for.

        internal static Size MeasureNaturalSize(TextAnnotation annotation)
        {
            if (annotation == null || string.IsNullOrEmpty(annotation.Text))
            {
                return new Size(0, 0);
            }

            var typeface = new Typeface(
                annotation.FontFamily,
                annotation.IsItalic ? FontStyle.Italic : FontStyle.Normal,
                annotation.IsBold ? FontWeight.Bold : FontWeight.Normal);

            var formattedText = new FormattedText(
                annotation.Text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                annotation.FontSize,
                Brushes.Black);

            double strokePadding = annotation.StrokeWidth;
            double padding = TextPadding * 2;
            // Spaces typed at the end count, as they do in the box the text was typed in: without them the
            // finished text was narrower than that box and the letters moved when it was finished.
            double width = formattedText.WidthIncludingTrailingWhitespace + padding + (strokePadding * 2);
            double height = formattedText.Height + padding + (strokePadding * 2);

            if (double.IsNaN(width) || width < 0) width = 0;
            if (double.IsNaN(height) || height < 0) height = 0;

            return new Size(width, height);
        }

        // The height a text needs in an annotation of the given width: its lines are broken to that width the way
        // DrawText breaks them.
        internal static double MeasureHeightAtWidth(TextAnnotation annotation, double width)
        {
            if (annotation == null || string.IsNullOrEmpty(annotation.Text))
            {
                return 0;
            }

            var typeface = new Typeface(
                annotation.FontFamily,
                annotation.IsItalic ? FontStyle.Italic : FontStyle.Normal,
                annotation.IsBold ? FontWeight.Bold : FontWeight.Normal);

            var formattedText = new FormattedText(
                annotation.Text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                annotation.FontSize,
                Brushes.Black);

            formattedText.Trimming = TextTrimming.None;

            double inset = (TextPadding + annotation.StrokeWidth) * 2;
            double maxTextWidth = Math.Max(0, width - inset);
            if (maxTextWidth > 0)
            {
                formattedText.MaxTextWidth = maxTextWidth;
            }

            double height = formattedText.Height + inset;
            return double.IsNaN(height) || height < 0 ? 0 : height;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            if (Annotation == null || string.IsNullOrEmpty(Annotation.Text)) return;

            DrawBox(context, Annotation, Bounds.Size);
            DrawText(context, Annotation, Annotation.Text, Bounds.Size);
        }

        // The box behind the text: the annotation's fill colour over the whole annotation, with rounded corners.
        // A transparent fill draws nothing, which is how text looked before the box existed.
        internal static void DrawBox(DrawingContext context, TextAnnotation annotation, Size size)
        {
            if (!annotation.HasBox || !Color.TryParse(annotation.FillColor, out Color fill)) return;

            double radius = Math.Clamp(annotation.CornerRadius, 0, Math.Min(size.Width, size.Height) / 2);
            context.DrawRectangle(new SolidColorBrush(fill), null, new Rect(size), radius, radius);
        }

        // The letters with their outline, laid out in an annotation of the given size. The box a text is typed in
        // draws its text with this too (TextAnnotationEditorBox), so it shows what the finished text will be.
        // scroll is how far the box being typed in has scrolled text that does not fit it; the finished text has none.
        internal static void DrawText(DrawingContext context, TextAnnotation annotation, string? text, Size size, Vector scroll = default)
        {
            if (string.IsNullOrEmpty(text)) return;

            var typeface = new Typeface(
                annotation.FontFamily,
                annotation.IsItalic ? FontStyle.Italic : FontStyle.Normal,
                annotation.IsBold ? FontWeight.Bold : FontWeight.Normal);

            double strokePadding = annotation.StrokeWidth;
            double horizontalPadding = TextPadding + strokePadding;
            double verticalPadding = TextPadding + strokePadding;
            double maxTextWidth = Math.Max(0, size.Width - (horizontalPadding * 2));

            var formattedText = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                annotation.FontSize,
                Brushes.Black); // Avalonia requires a brush to properly construct text extents in some backends

            // A word too long for a line is broken over lines, as the text box does while it is typed. The default
            // replaced it with "…", in the saved image too.
            formattedText.Trimming = TextTrimming.None;

            if (maxTextWidth > 0)
            {
                formattedText.MaxTextWidth = maxTextWidth;
            }

            formattedText.TextAlignment = TextHorizontalAlignmentHelper.ToAvaloniaTextAlignment(annotation.HorizontalAlignment);

            double textHeight = formattedText.Height;
            double originX = horizontalPadding;
            double originY = Math.Max(verticalPadding, (size.Height - textHeight) / 2.0);

            originY = Math.Min(originY, Math.Max(verticalPadding, size.Height - textHeight - verticalPadding));

            var textGeometry = formattedText.BuildGeometry(new Point(originX - scroll.X, originY - scroll.Y));
            if (textGeometry == null) return;

            // Text color is TextColor, outline color is StrokeColor. A transparent one is not drawn.
            IBrush? fillBrush = null;
            if (!string.IsNullOrEmpty(annotation.TextColor))
            {
                var textColor = Color.Parse(annotation.TextColor);
                if (textColor.A > 0)
                {
                    fillBrush = new SolidColorBrush(textColor);
                }
            }

            IPen? strokePen = null;
            if (annotation.HasVisibleOutline)
            {
                strokePen = new Pen(new SolidColorBrush(Color.Parse(annotation.StrokeColor)), annotation.StrokeWidth, lineJoin: PenLineJoin.Round);
            }

            // Letters that do not fit a shrunk annotation are cut at its edge.
            using (context.PushClip(new Rect(size)))
            {
                // The outline first, so the letters are drawn on top of it.
                if (strokePen != null)
                {
                    context.DrawGeometry(null, strokePen, textGeometry);
                }

                if (fillBrush != null)
                {
                    context.DrawGeometry(fillBrush, null, textGeometry);
                }
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (Annotation == null || string.IsNullOrEmpty(Annotation.Text))
            {
                return new Size(0, 0);
            }

            var typeface = new Typeface(
                Annotation.FontFamily,
                Annotation.IsItalic ? FontStyle.Italic : FontStyle.Normal,
                Annotation.IsBold ? FontWeight.Bold : FontWeight.Normal);

            var formattedText = new FormattedText(
                Annotation.Text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                Annotation.FontSize,
                Brushes.Black);

            double strokePadding = Annotation.StrokeWidth;
            double padding = TextPadding * 2;
            var annotationBounds = Annotation.GetBounds();
            double boundedWidth = Math.Max(0, annotationBounds.Width - padding - (strokePadding * 2));

            formattedText.Trimming = TextTrimming.None; // as DrawText lays it out

            if (boundedWidth > 0)
            {
                formattedText.MaxTextWidth = boundedWidth;
            }

            double width = Math.Max(annotationBounds.Width, formattedText.Width + padding + (strokePadding * 2));
            double height = Math.Max(annotationBounds.Height, formattedText.Height + padding + (strokePadding * 2));

            if (double.IsNaN(width) || width < 0) width = 0;
            if (double.IsNaN(height) || height < 0) height = 0;

            return new Size(width, height);
        }
    }
}