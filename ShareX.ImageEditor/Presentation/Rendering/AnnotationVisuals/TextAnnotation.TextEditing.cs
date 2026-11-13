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
using ShareX.ImageEditor.Presentation.Controls;
using ShareX.ImageEditor.Presentation.Helpers;
using SkiaSharp;

namespace ShareX.ImageEditor.Core.Annotations;

public partial class TextAnnotation
{
    internal TextBox CreateCreationTextEditor(SolidColorBrush brush)
    {
        var textBrush = Avalonia.Media.Color.TryParse(TextColor, out var c) ? new SolidColorBrush(c) : brush;
        var textBox = new TextAnnotationEditorBox(this, textBrush)
        {
            BorderThickness = new Thickness(EditorBorderThickness),
            BorderBrush = GetEditorBorderBrush(),
            CornerRadius = new CornerRadius(Math.Max(0, CornerRadius)),
            FontSize = FontSize,
            FontFamily = new Avalonia.Media.FontFamily(FontFamily),
            FontWeight = IsBold ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal,
            FontStyle = IsItalic ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal,
            TextAlignment = TextHorizontalAlignmentHelper.ToAvaloniaTextAlignment(HorizontalAlignment),
            HorizontalContentAlignment = TextHorizontalAlignmentHelper.ToHorizontalContentAlignment(HorizontalAlignment),
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Text = string.Empty,
            Padding = new Thickness(GetEditorPadding()),
            AcceptsReturn = false,
            Tag = this,
            MinWidth = 0,
            MinHeight = 0
        };

        // It grows from the click point as its alignment holds it: rightwards, both ways, or leftwards.
        textBox.FollowAnchor();

        if (ShadowEnabled)
        {
            textBox.Effect = ShareX.ImageEditor.Presentation.Helpers.ShadowEffectHelper.CreateDropShadow(this);
        }

        return textBox;
    }

    internal void SetCreatedText(TextBox textBox)
    {
        Text = textBox.Text ?? string.Empty;
        Size naturalSize = OutlinedTextControl.MeasureNaturalSize(this);
        double width = naturalSize.Width > 0 ? naturalSize.Width : textBox.Bounds.Width;
        double height = naturalSize.Height > 0 ? naturalSize.Height : textBox.Bounds.Height;

        // Whole pixels, stored so that they are never a hair over (SetSize), as an edited text gets them: a raw size
        // stored as floats could lay out one pixel off the box the text was typed in. The rectangle is the click
        // point until now; it grows from there as the text box did (SetSizeAnchored).
        SetSizeAnchored(Math.Ceiling(width), Math.Ceiling(height));
    }

    // A rectangle within this of the text's own size counts as never resized (sizes are stored as floats and
    // rounded up to whole pixels).
    private const double NaturalSizeTolerance = 1.5;

    /// <summary>
    /// True when the rectangle is still the size the text itself takes, as it is after typing a new text. False
    /// once it has been dragged to another size.
    /// </summary>
    internal bool HasNaturalSize()
    {
        Size naturalSize = OutlinedTextControl.MeasureNaturalSize(this);
        SKRect bounds = GetBounds();
        return naturalSize.Width > 0 &&
            Math.Abs(bounds.Width - naturalSize.Width) <= NaturalSizeTolerance &&
            Math.Abs(bounds.Height - naturalSize.Height) <= NaturalSizeTolerance;
    }

    /// <summary>
    /// Sizes the box a finished text is edited in again (CreateTextEditor), so it follows the text while it is
    /// typed. A text that still has its own size gets a free box, as a new text has: it grows and shrinks with the
    /// lines. A rectangle that was resized keeps its width, where the lines wrap, and grows downwards only.
    /// </summary>
    internal void SizeTextEditor(TextBox textBox)
    {
        if (HasNaturalSize())
        {
            // As the box a new text is typed in: no smallest width, only typed line breaks, and it grows from the
            // point its alignment keeps still.
            textBox.Width = double.NaN;
            textBox.Height = double.NaN;
            textBox.MinWidth = 0;
            textBox.TextWrapping = TextWrapping.NoWrap;
            (textBox as TextAnnotationEditorBox)?.FollowAnchor();
        }
        else
        {
            SKRect bounds = GetBounds();
            textBox.Width = Math.Max(20, bounds.Width);
            textBox.Height = double.NaN;
            textBox.MinHeight = Math.Max(20, bounds.Height);
        }
    }

    /// <summary>
    /// Takes the text from the box it was edited in (see SizeTextEditor) and fits the rectangle to it, from its
    /// top left corner. Before, the rectangle stayed as it was and lines added to the text were cut off.
    /// </summary>
    internal void SetEditedText(TextBox textBox)
    {
        Text = textBox.Text ?? string.Empty;
        FitToText(free: double.IsNaN(textBox.Width));
    }

    /// <summary>
    /// Changes the font, its size or weight, or the outline width, all of which change the size the text takes.
    /// A rectangle that had the text's own size before keeps having it, so the box behind a text follows a
    /// font size picked after the text was finished. A resized rectangle is left as it is. Returns true when
    /// the rectangle changed.
    /// </summary>
    internal bool ChangeStyle(Action change)
    {
        Size before = OutlinedTextControl.MeasureNaturalSize(this);
        bool natural = HasNaturalSize();
        change();

        // Picking a text sets its own values again; that is no change and must not move anything.
        if (!natural || OutlinedTextControl.MeasureNaturalSize(this) == before)
        {
            return false;
        }

        FitToText(free: true);
        return true;
    }

    // Fits the rectangle to the text. Free: it becomes the text's own size, growing and shrinking from the point its
    // alignment keeps still (SetSizeAnchored). Not free (a rectangle that was resized): it keeps its width, where the
    // lines wrap, and is made taller downwards when they need it.
    private void FitToText(bool free)
    {
        SKRect bounds = GetBounds();

        if (free)
        {
            Size naturalSize = OutlinedTextControl.MeasureNaturalSize(this);
            if (naturalSize.Width > 0 && naturalSize.Height > 0) // no text left: the rectangle stays
            {
                SetSizeAnchored(Math.Ceiling(naturalSize.Width), Math.Ceiling(naturalSize.Height));
            }
        }
        else
        {
            double needed = Math.Ceiling(OutlinedTextControl.MeasureHeightAtWidth(this, bounds.Width));
            if (needed > bounds.Height)
            {
                SetSize(bounds.Width, needed);
            }
        }
    }

    /// <summary>
    /// Gives the rectangle a size, from its top left corner. The corners are stored as floats, so the stored size
    /// can come out a hair over the one asked for, and layout rounds any size up to whole pixels: the finished text
    /// was then one pixel bigger than the box it was typed in. The far corner is stepped down until it is not over.
    /// </summary>
    internal void SetSize(double width, double height)
    {
        SKRect bounds = GetBounds();
        SetRect(bounds.Left, bounds.Top, width, height);
    }

    /// <summary>
    /// Gives a rectangle that was never resized a new size, keeping still the point its alignment holds: the top left
    /// corner for left-aligned text, the middle of the top edge for centred text, the top right corner for
    /// right-aligned text. So a centred text grows both ways from its middle and a right-aligned one leftwards.
    /// </summary>
    internal void SetSizeAnchored(double width, double height)
    {
        Point topLeft = GetAnchoredTopLeft(GetStoredBounds(), width, height, HorizontalAlignment, RotationAngle);
        SetRect(topLeft.X, topLeft.Y, width, height);
    }

    /// <summary>
    /// The rectangle as stored, without the smallest size GetBounds gives it: for a new text that is still its click
    /// point, so a centred or right-aligned text is held at the click itself, not 5 or 10 pixels to its right.
    /// </summary>
    internal SKRect GetStoredBounds()
    {
        return new SKRect(
            Math.Min(StartPoint.X, EndPoint.X),
            Math.Min(StartPoint.Y, EndPoint.Y),
            Math.Max(StartPoint.X, EndPoint.X),
            Math.Max(StartPoint.Y, EndPoint.Y));
    }

    /// <summary>
    /// Stores the rectangle the edit box has now, so a later refit anchors on this box and not on the one from
    /// when editing started. See <see cref="TextAnnotationEditorBox.RebaseAnchor"/>.
    /// </summary>
    internal void SetStoredRect(double x, double y, double width, double height) => SetRect(x, y, width, height);

    /// <summary>
    /// Where the top left corner of a rectangle of the new size goes so that the point its alignment holds (see
    /// SetSizeAnchored) stays where it is on the page. Measured in the rectangle's own frame: a turned text turns
    /// about its middle, so for it both corners move, and the text itself does not.
    /// </summary>
    internal static Point GetAnchoredTopLeft(SKRect from, double width, double height, TextHorizontalAlignment alignment, double angle)
    {
        double anchor = alignment switch
        {
            TextHorizontalAlignment.Center => 0.5,
            TextHorizontalAlignment.Right => 1,
            _ => 0
        };

        if (angle == 0)
        {
            return new Point(from.Left + (anchor * (from.Width - width)), from.Top);
        }

        double radians = angle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);

        // The held point on the page: offset from the old middle, turned.
        double fromX = (anchor * from.Width) - (from.Width / 2), fromY = -from.Height / 2;
        double pageX = from.MidX + (fromX * cos) - (fromY * sin);
        double pageY = from.MidY + (fromX * sin) + (fromY * cos);

        // The new middle that puts the same point of the new size there.
        double toX = (anchor * width) - (width / 2), toY = -height / 2;
        double middleX = pageX - ((toX * cos) - (toY * sin));
        double middleY = pageY - ((toX * sin) + (toY * cos));
        return new Point(middleX - (width / 2), middleY - (height / 2));
    }

    // Stores a rectangle so that its size is never a hair over the one asked for (see SetSize).
    private void SetRect(double x, double y, double width, double height)
    {
        float left = (float)x, top = (float)y;
        float right = (float)(left + width), bottom = (float)(top + height);

        while (right > left && (double)(right - left) > width) right = MathF.BitDecrement(right);
        while (bottom > top && (double)(bottom - top) > height) bottom = MathF.BitDecrement(bottom);

        StartPoint = new SKPoint(left, top);
        EndPoint = new SKPoint(right, bottom);
    }

    private const double EditorBorderThickness = 1;

    // Thin and neutral: it only marks the box as being typed in, and goes when the text is finished.
    private static readonly IBrush EditorBorderBrush = new SolidColorBrush(Color.FromArgb(160, 128, 128, 128));

    // With a shadow the marker border would cast one too, a line the finished text does not have.
    private IBrush GetEditorBorderBrush() => ShadowEnabled ? Brushes.Transparent : EditorBorderBrush;

    /// <summary>True when the text has a box behind it: a fill colour that is not transparent.</summary>
    internal bool HasBox => Color.TryParse(FillColor, out Color fill) && fill.A > 0;

    /// <summary>True when the letters have an outline that can be seen.</summary>
    internal bool HasVisibleOutline => StrokeWidth > 0 && Color.TryParse(StrokeColor, out Color stroke) && stroke.A > 0;

    // The finished text keeps this much room around it (see OutlinedTextControl); the text box border is part of it,
    // so the caret and the selection sit on the letters the box draws.
    private double GetEditorPadding()
    {
        return Math.Max(0, OutlinedTextControl.TextPadding + StrokeWidth - EditorBorderThickness);
    }

    internal TextBox CreateTextEditor()
    {
        string textColor = TextColor;
        if (string.IsNullOrEmpty(textColor) || textColor == "#00000000")
        {
            textColor = string.IsNullOrEmpty(StrokeColor) || StrokeColor == "#00000000"
                ? "#FF000000"
                : StrokeColor;
        }

        var textBrush = new SolidColorBrush(Color.Parse(textColor));
        var textBox = new TextAnnotationEditorBox(this, textBrush)
        {
            Text = Text,
            BorderThickness = new Thickness(EditorBorderThickness),
            BorderBrush = GetEditorBorderBrush(),
            CornerRadius = new CornerRadius(Math.Max(0, CornerRadius)),
            FontSize = FontSize,
            FontFamily = new Avalonia.Media.FontFamily(string.IsNullOrWhiteSpace(FontFamily) ? "Segoe UI" : FontFamily),
            FontWeight = IsBold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = IsItalic ? FontStyle.Italic : FontStyle.Normal,
            Padding = new Thickness(GetEditorPadding()),
            AcceptsReturn = false,
            TextAlignment = TextHorizontalAlignmentHelper.ToAvaloniaTextAlignment(HorizontalAlignment),
            HorizontalContentAlignment = TextHorizontalAlignmentHelper.ToHorizontalContentAlignment(HorizontalAlignment),
            VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 20,
            MinHeight = 0,
            Tag = this
        };

        if (ShadowEnabled)
        {
            textBox.Effect = ShareX.ImageEditor.Presentation.Helpers.ShadowEffectHelper.CreateDropShadow(this);
        }

        // Apply rotation to make editing match display
        if (RotationAngle != 0)
        {
            textBox.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            textBox.RenderTransform = new RotateTransform(RotationAngle);
        }

        return textBox;
    }
}
