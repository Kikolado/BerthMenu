using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using BerthMenu.Services;

namespace BerthMenu.Views
{
    /// <summary>Picks one color for a Settings "Custom color" choice. Shows
    /// <see cref="SelectedColor"/> when opened; it holds the choice after OK.</summary>
    public partial class ColorPickerDialog : Window
    {
        // Three rows of ten: grays, bright colors, deeper versions of them.
        private static readonly string[] Palette =
        {
            "#FFFFFF", "#E6E6E6", "#C8C8C8", "#A0A0A0", "#787878", "#505050", "#333333", "#202020", "#121212", "#000000",
            "#E81123", "#F7630C", "#FFB900", "#16C60C", "#00B294", "#0099BC", "#0078D4", "#8764B8", "#B146C2", "#E3008C",
            "#A4262C", "#CA5010", "#986F0B", "#107C10", "#00796B", "#005B70", "#004E8C", "#5C2D91", "#881798", "#9B0062",
        };

        private readonly List<Button> _swatches = new();
        private bool _updatingHex;

        public Color SelectedColor { get; private set; }

        public ColorPickerDialog(Color current, string title)
        {
            InitializeComponent();
            ThemeService.ApplyTitleBarTheme(this);
            Title = title;

            foreach (string hex in Palette)
            {
                var swatch = new Button
                {
                    Style = (Style)FindResource("SwatchButton"),
                    Background = ColorUtil.Brush(ColorUtil.Parse(hex, Colors.Black)),
                    Margin = new Thickness(0, 0, 6, 6),
                    ToolTip = hex,
                };
                swatch.Click += Swatch_Click;
                PalettePanel.Children.Add(swatch);
                _swatches.Add(swatch);
            }

            WindowsAccentSwatch.Background = ColorUtil.Brush(ColorUtil.WindowsAccent());
            _swatches.Add(WindowsAccentSwatch);

            Select(current, updateHex: true);
            Loaded += (_, _) =>
            {
                HexBox.Focus();
                HexBox.SelectAll();
            };
        }

        private void Swatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Background: SolidColorBrush brush })
                Select(brush.Color, updateHex: true);
        }

        private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingHex)
                return;
            bool valid = ColorUtil.TryParse(HexBox.Text, out var color);
            OkButton.IsEnabled = valid;
            if (valid)
                Select(color, updateHex: false);
        }

        /// <summary>Makes <paramref name="color"/> the choice: preview, ring around a
        /// matching swatch, and the hex box (unless the hex box is where it came from).</summary>
        private void Select(Color color, bool updateHex)
        {
            SelectedColor = Color.FromRgb(color.R, color.G, color.B);
            PreviewBorder.Background = ColorUtil.Brush(SelectedColor);
            foreach (var swatch in _swatches)
                swatch.Tag = swatch.Background is SolidColorBrush b && b.Color == SelectedColor ? "Selected" : null;

            if (updateHex)
            {
                _updatingHex = true;
                HexBox.Text = ColorUtil.ToHex(SelectedColor);
                _updatingHex = false;
                OkButton.IsEnabled = true;
            }
        }

        /// <summary>The full Windows color picker, for anything the swatches don't have.</summary>
        private void MoreColors_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                FullOpen = true,
                AnyColor = true,
                Color = System.Drawing.Color.FromArgb(SelectedColor.R, SelectedColor.G, SelectedColor.B),
            };
            var owner = new OwnerWindow(new WindowInteropHelper(this).Handle);
            if (dialog.ShowDialog(owner) == System.Windows.Forms.DialogResult.OK)
                Select(Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B), updateHex: true);
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (!ColorUtil.TryParse(HexBox.Text, out var color))
            {
                HexBox.Focus();
                return;
            }
            SelectedColor = color;
            DialogResult = true;
        }

        /// <summary>Lets the WinForms color dialog sit on top of this window.</summary>
        private sealed class OwnerWindow : System.Windows.Forms.IWin32Window
        {
            public OwnerWindow(IntPtr handle) => Handle = handle;
            public IntPtr Handle { get; }
        }
    }
}
