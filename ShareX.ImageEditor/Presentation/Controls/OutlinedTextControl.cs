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
            Color textColor = string.IsNullOrEmpty(annotation.TextColor) ? default : Color.Parse(annotation.TextColor);
            bool hasOutline = annotation.HasVisibleOutline;
            if (textColor.A == 0 && !hasOutline) return;
            IBrush glyphBrush = textColor.A == 0 ? Brushes.Black :
                new SolidColorBrush(Color.FromRgb(textColor.R, textColor.G, textColor.B));

            FormattedText Layout(string value)
            {
                var layout = new FormattedText(
                    value,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    annotation.FontSize,
                    glyphBrush);

                // A word too long for a line is broken over lines, as the text box does while it is typed. The
                // default replaced it with "…", in the saved image too.
                layout.Trimming = TextTrimming.None;

                if (maxTextWidth > 0)
                {
                    layout.MaxTextWidth = maxTextWidth;
                }

                layout.TextAlignment = TextHorizontalAlignmentHelper.ToAvaloniaTextAlignment(annotation.HorizontalAlignment);
                return layout;
            }

            var formattedText = Layout(text);

            // A text that ends in a line break (Ctrl+Enter at the end) has a new, empty last line. The text box counts
            // it: the caret goes there and the box grows by a line. FormattedText.Height does not, so the letters were
            // centred in the box half a line lower than the text box's own lines, and the caret on the empty line sat
            // on top of them. A space on that line counts it without drawing anything.
            double textHeight = EndsWithLineBreak(text)
                ? Layout(text + " ").Height
                : formattedText.Height;
            double originX = horizontalPadding;
            double originY = Math.Max(verticalPadding, (size.Height - textHeight) / 2.0);

            originY = Math.Min(originY, Math.Max(verticalPadding, size.Height - textHeight - verticalPadding));

            var origin = new Point(originX - scroll.X, originY - scroll.Y);
            IPen? strokePen = null;
            if (hasOutline)
            {
                strokePen = new Pen(new SolidColorBrush(Color.Parse(annotation.StrokeColor)), annotation.StrokeWidth, lineJoin: PenLineJoin.Round);
            }

            // Letters that do not fit a shrunk annotation are cut at its edge.
            using (context.PushClip(new Rect(size)))
            {
                // The outline first, then the original shaped layout so fallback glyphs retain their baselines.
                if (strokePen != null)
                {
                    // Colour glyphs may have no vector outline; geometry is only used for the stroke.
                    var textGeometry = formattedText.BuildGeometry(origin);
                    if (textGeometry != null) context.DrawGeometry(null, strokePen, textGeometry);
                }

                if (textColor.A > 0)
                {
                    // Fade the whole fill once: colour-font palettes need opacity as well as the text brush.
                    // Greyscale smoothing: the default subpixel smoothing left coloured fringes on the letters in the
                    // saved image. Colour emoji keep their colours.
                    using (context.PushRenderOptions(new RenderOptions { TextRenderingMode = TextRenderingMode.Antialias }))
                    using (context.PushOpacity(textColor.A / 255.0))
                    {
                        context.DrawText(formattedText, origin);
                    }
                }
            }
        }

        // Every character the text layout ends a line on: line feed, carriage return, next line (U+0085), line and
        // paragraph separator (U+2028, U+2029), vertical tab and form feed. Typing only gives the first; the rest can
        // come in by paste (review of 389f3460a).
        private static bool EndsWithLineBreak(string text)
        {
            return text.Length > 0 && text[^1] is '\n' or '\r' or '\u0085' or '\u2028' or '\u2029' or '\v' or '\f';
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
