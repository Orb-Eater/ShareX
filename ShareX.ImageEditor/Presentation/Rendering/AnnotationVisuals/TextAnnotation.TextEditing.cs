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
        EndPoint = new SKPoint(
            (float)(Canvas.GetLeft(textBox) + width),
            (float)(Canvas.GetTop(textBox) + height));
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
