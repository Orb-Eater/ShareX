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

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShareX.ImageEditor.Presentation.Views;

namespace ShareX.ImageEditor.Presentation.Controls
{
    public partial class StrengthSlider : UserControl
    {
        public static readonly StyledProperty<float> SelectedStrengthProperty =
            AvaloniaProperty.Register<StrengthSlider, float>(
                nameof(SelectedStrength),
                defaultValue: 10);

        public static readonly StyledProperty<float> MinimumProperty =
            AvaloniaProperty.Register<StrengthSlider, float>(
                nameof(Minimum),
                defaultValue: 1);

        public static readonly StyledProperty<float> MaximumProperty =
            AvaloniaProperty.Register<StrengthSlider, float>(
                nameof(Maximum),
                defaultValue: 30);

        public float SelectedStrength
        {
            get => GetValue(SelectedStrengthProperty);
            set => SetValue(SelectedStrengthProperty, value);
        }

        public float Minimum
        {
            get => GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public float Maximum
        {
            get => GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public event EventHandler<float>? StrengthChanged;

        private readonly Border _host;
        private readonly TextBlock _label;
        private readonly StrengthValueBox _editor;
        private bool _editing;
        private float _valueBeforeEdit;
        private Key? _completionKey;

        static StrengthSlider()
        {
            SelectedStrengthProperty.Changed.AddClassHandler<StrengthSlider>((s, e) =>
            {
                float roundedStrength = MathF.Round(s.SelectedStrength);

                if (Math.Abs(s.SelectedStrength - roundedStrength) > float.Epsilon)
                {
                    s.SelectedStrength = roundedStrength;
                    return;
                }

                s.StrengthChanged?.Invoke(s, roundedStrength);
                s.SyncEditingText();
            });
        }

        public StrengthSlider()
        {
            AvaloniaXamlLoader.Load(this);

            _host = this.FindControl<Border>("ValueHost")!;
            _label = this.FindControl<TextBlock>("ValueLabel")!;
            _editor = new StrengthValueBox();
            _editor.LostFocus += OnEditorLostFocus;
            // Tunnel, so Enter, Escape and the arrows are taken before the text box
            // moves the caret and before the key bubbles to the editor window.
            _editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
            _editor.AddHandler(KeyUpEvent, OnEditorKeyUp, RoutingStrategies.Tunnel);
            this.FindControl<Grid>("ValueHostGrid")!.Children.Add(_editor);
        }

        // Click opens the field with the whole number selected, so typing replaces it.
        private void OnValuePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_editing || !e.GetCurrentPoint(_host).Properties.IsLeftButtonPressed)
            {
                return;
            }

            e.Handled = true;
            BeginEdit();
        }

        private void BeginEdit()
        {
            if (_editing)
            {
                return;
            }

            _editing = true;
            _valueBeforeEdit = SelectedStrength;
            _completionKey = null;
            _host.Classes.Add("editing");
            _editor.Text = FormatStrength(SelectedStrength);
            _label.IsVisible = false;
            _editor.IsVisible = true;

            // After the click has finished, so the press itself does not place the caret.
            Dispatcher.UIThread.Post(() =>
            {
                if (!_editing)
                {
                    return;
                }

                _editor.Focus();
                _editor.SelectAll();
            }, DispatcherPriority.Input);
        }

        private void OnEditorKeyDown(object? sender, KeyEventArgs e)
        {
            if (!_editing || e.Handled)
            {
                return;
            }

            if (e.Key == Key.Up)
            {
                e.Handled = true;
                Nudge(1);
                return;
            }

            if (e.Key == Key.Down)
            {
                e.Handled = true;
                Nudge(-1);
                return;
            }

            if (e.Key is Key.Enter or Key.Escape)
            {
                e.Handled = true;
                _completionKey = e.Key;
            }
        }

        private void OnEditorKeyUp(object? sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Enter or Key.Escape))
            {
                return;
            }

            // Mark it handled before leaving the field. Otherwise Escape's key-up
            // reaches the editor window after the focus has already moved, and the
            // window treats it as "close the editor".
            bool complete = _completionKey == e.Key;
            _completionKey = null;
            e.Handled = true;

            if (!complete || !_editing)
            {
                return;
            }

            EndEdit(commit: e.Key == Key.Enter, returnFocus: true);
        }

        private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
        {
            // Enter and Escape move the focus themselves. A click elsewhere commits on the way out.
            EndEdit(commit: true, returnFocus: false);
        }

        // Arrows change the number in the field, including digits that are typed but not committed yet.
        private void Nudge(int delta)
        {
            float current = TryParseStrength(_editor.Text, out float parsed)
                ? MathF.Round(parsed)
                : MathF.Round(SelectedStrength);
            float next = Math.Clamp(current + delta, Minimum, Maximum);
            SelectedStrength = next;

            if (_editor.Text != FormatStrength(next))
            {
                _editor.Text = FormatStrength(next);
                _editor.SelectAll();
            }
        }

        private void EndEdit(bool commit, bool returnFocus)
        {
            if (!_editing)
            {
                return;
            }

            _editing = false;
            _host.Classes.Remove("editing");

            if (commit)
            {
                ApplyEditorText();
            }
            else if (SelectedStrength != _valueBeforeEdit)
            {
                // Escape undoes the whole edit, the steps made with the arrow keys included.
                SelectedStrength = _valueBeforeEdit;
            }

            _editor.IsVisible = false;
            _label.IsVisible = true;

            if (returnFocus)
            {
                ReturnFocusToEditor();
            }
        }

        // One write to SelectedStrength. The property handler rounds, raises StrengthChanged
        // once, and the slider binding follows. Empty or junk text writes nothing.
        private void ApplyEditorText()
        {
            if (!TryParseStrength(_editor.Text, out float parsed))
            {
                return;
            }

            SelectedStrength = Math.Clamp(MathF.Round(parsed), Minimum, Maximum);
        }

        private void SyncEditingText()
        {
            if (!_editing)
            {
                return;
            }

            string text = FormatStrength(SelectedStrength);
            if (_editor.Text == text)
            {
                return;
            }

            _editor.Text = text;
            _editor.SelectAll();
        }

        private static string FormatStrength(float value) =>
            MathF.Round(value).ToString("F0", CultureInfo.CurrentCulture);

        // "7", "7.6" and "7,6". Anything else (blank, words, two separators) is refused.
        private static bool TryParseStrength(string? text, out float value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string trimmed = text.Trim();
            int separators = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c is '.' or ',')
                {
                    if (++separators > 1)
                    {
                        return false;
                    }
                }
                else if (c == '-' && i == 0)
                {
                    continue;
                }
                else if (!char.IsAsciiDigit(c))
                {
                    return false;
                }
            }

            return float.TryParse(trimmed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && float.IsFinite(value);
        }

        private void ReturnFocusToEditor()
        {
            EditorView? editor = this.GetVisualAncestors().OfType<EditorView>().FirstOrDefault();
            if (editor != null && editor.Focus())
            {
                return;
            }

            TopLevel.GetTopLevel(this)?.Focus();
        }

        /// <summary>
        /// The number while it is being typed. Compact on purpose: the toolbar row is 28px tall, and the
        /// app theme gives every text box a 40px minimum, padding and a border by styling the template
        /// parts directly. Values set on those parts win over that, in both themes.
        /// </summary>
        private sealed class StrengthValueBox : TextBox
        {
            // Styled and templated as an ordinary text box, then shrunk below.
            protected override Type StyleKeyOverride => typeof(TextBox);

            public StrengthValueBox()
            {
                // 4px wider than the 24px number slot, into the gap before the slider. The slot does not grow, so
                // the toolbar does not jump, and the whole field stays inside the control: a part sticking out on
                // the left could be seen but not clicked. 28px fits three digits and "7,6".
                Width = 28;
                Height = 18;
                MinWidth = 0;
                MinHeight = 0;
                Padding = new Thickness(0);
                BorderThickness = new Thickness(1);
                CornerRadius = new CornerRadius(2);
                FontSize = 12;
                TextAlignment = TextAlignment.Center;
                HorizontalContentAlignment = global::Avalonia.Layout.HorizontalAlignment.Center;
                VerticalContentAlignment = global::Avalonia.Layout.VerticalAlignment.Center;
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left;
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center;
                FocusAdorner = null;
                // The theme's Cut/Copy/Paste menu takes the focus when it opens, which would end the edit and
                // commit a half-typed number. Ctrl+V still pastes.
                ContextFlyout = null;
                IsVisible = false;

                this[!ForegroundProperty] = new DynamicResourceExtension("ShareX.Brush.Text");
                this[!BackgroundProperty] = new DynamicResourceExtension("ShareX.Brush.Control.Background");
                this[!BorderBrushProperty] = new DynamicResourceExtension("ShareX.Brush.Border");
                this[!CaretBrushProperty] = new DynamicResourceExtension("ShareX.Brush.Text");
            }

            protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
            {
                base.OnApplyTemplate(e);

                if (e.NameScope.Find<Border>("PART_BorderElement") is { } border)
                {
                    border.MinWidth = 0;
                    border.MinHeight = 0;
                    border.Padding = new Thickness(0);
                    border.Margin = new Thickness(0);
                    border.BorderThickness = new Thickness(1);
                    border.CornerRadius = new CornerRadius(2);
                    border[!Border.BackgroundProperty] = new DynamicResourceExtension("ShareX.Brush.Control.Background");
                    border[!Border.BorderBrushProperty] = new DynamicResourceExtension("ShareX.Brush.Border");
                }

                if (e.NameScope.Find<TextPresenter>("PART_TextPresenter") is { } presenter)
                {
                    presenter.MinHeight = 0;
                    presenter.TextAlignment = TextAlignment.Center;
                    presenter.VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center;
                    presenter[!TextElement.ForegroundProperty] = new DynamicResourceExtension("ShareX.Brush.Text");
                }

                if (e.NameScope.Find<ScrollViewer>("PART_ScrollViewer") is { } scrollViewer)
                {
                    scrollViewer.MinHeight = 0;
                    scrollViewer.Padding = new Thickness(0);
                    scrollViewer.Margin = new Thickness(0);
                }
            }
        }
    }
}