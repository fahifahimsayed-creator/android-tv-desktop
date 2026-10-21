using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AndroidTVDesktop
{
    /// <summary>Small builders so pages read like layout code.</summary>
    static class Ui
    {
        public static TextBlock H1(string t) => new TextBlock { Text = t, FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        public static TextBlock Sub(string t) => new TextBlock { Text = t, Foreground = Theme.B(Theme.SubText), Margin = new Thickness(0, 0, 0, 18) };
        public static TextBlock H2(string t) => new TextBlock { Text = t, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
        public static TextBlock P(string t, bool dim = false) => new TextBlock { Text = t, Foreground = Theme.B(dim ? Theme.SubText : Theme.Text), Margin = new Thickness(0, 2, 0, 6) };
        public static TextBlock Mono(string t) => new TextBlock { Text = t, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, Foreground = Theme.B(Theme.SubText), TextWrapping = TextWrapping.Wrap };

        public static Border Card(params UIElement[] children)
        {
            var sp = new StackPanel();
            foreach (var c in children) if (c != null) sp.Children.Add(c);
            return new Border
            {
                Background = Theme.B(Theme.Card), BorderBrush = Theme.B(Theme.Border), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 18, 20, 12), Margin = new Thickness(0, 0, 0, 16), Child = sp
            };
        }

        public static Button Btn(string text, Action onClick, bool primary = false, string tip = null)
        {
            var b = new Button { Content = text, ToolTip = tip };
            if (primary) b.Style = (Style)Application.Current.Resources["Primary"];
            b.Click += (s, e) => onClick();
            return b;
        }

        public static WrapPanel Buttons(params UIElement[] buttons)
        {
            var w = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var b in buttons) if (b != null) w.Children.Add(b);
            return w;
        }

        public static CheckBox Toggle(string text, bool value, Action<bool> changed, string tip = null)
        {
            var c = new CheckBox { Content = new TextBlock { Text = text }, IsChecked = value, ToolTip = tip };
            c.Checked += (s, e) => changed(true);
            c.Unchecked += (s, e) => changed(false);
            return c;
        }

        /// <summary>Label : value row, returns the value TextBlock so pages can update it.</summary>
        public static Grid Row(string label, out TextBlock value, string initial = "")
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = label, Foreground = Theme.B(Theme.SubText) });
            value = new TextBlock { Text = initial };
            Grid.SetColumn(value, 1);
            g.Children.Add(value);
            return g;
        }

        public static Ellipse Dot(Color c) => new Ellipse { Width = 10, Height = 10, Fill = Theme.B(c), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };

        public static StackPanel Horizontal(params UIElement[] items)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var i in items) sp.Children.Add(i);
            return sp;
        }

        public static ComboBox Combo<T>(T[] items, T selected, Func<T, string> label, Action<T> changed)
        {
            var cb = new ComboBox();
            foreach (var i in items) cb.Items.Add(new ComboBoxItem { Content = label(i), Tag = i });
            for (int k = 0; k < items.Length; k++) if (Equals(items[k], selected)) cb.SelectedIndex = k;
            cb.SelectionChanged += (s, e) => { if (cb.SelectedItem is ComboBoxItem it) changed((T)it.Tag); };
            return cb;
        }
    }
}
