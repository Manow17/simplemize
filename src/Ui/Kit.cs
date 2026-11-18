using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Simplemize.Ui
{
    /// <summary>Small factory helpers so the page code reads like a layout, not like plumbing.</summary>
    public static class Kit
    {
        public static T Res<T>(this T el, DependencyProperty p, string key) where T : FrameworkElement
        {
            el.SetResourceReference(p, key);
            return el;
        }

        public static TextBlock Text(string text, double size = 13.5, string brush = "Ink", string font = "FontBody",
                                     FontWeight? weight = null, bool wrap = true)
        {
            var t = new TextBlock
            {
                Text = text,
                FontSize = size,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                FontWeight = weight ?? FontWeights.Normal,
            };
            if (!wrap) t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            t.SetResourceReference(TextBlock.FontFamilyProperty, font);
            TextOptions.SetTextFormattingMode(t, size >= 20 ? TextFormattingMode.Ideal : TextFormattingMode.Display);
            return t;
        }

        public static TextBlock Mono(string text, double size = 11.5, string brush = "Ink3", bool wrap = true)
            => Text(text, size, brush, "FontMono", null, wrap);

        public static TextBlock Display(string text, double size, string brush = "Ink", FontWeight? weight = null)
            => Text(text, size, brush, "FontDisplay", weight ?? FontWeights.SemiBold);

        /// <summary>Uppercase label used for group headers and field names.</summary>
        public static TextBlock Label(string text, string brush = "Ink3", double size = 10.5)
        {
            var t = Mono(text.ToUpperInvariant(), size, brush, false);
            t.FontWeight = FontWeights.SemiBold;
            return t;
        }

        public static Run Run(string text, string brush = null, FontWeight? weight = null)
        {
            var r = new Run(text);
            if (brush != null) r.SetResourceReference(TextElement.ForegroundProperty, brush);
            if (weight != null) r.FontWeight = weight.Value;
            return r;
        }

        public static Border Rule(double thickness = 1, string brush = "Line", Thickness? margin = null)
        {
            var b = new Border { Height = thickness, Margin = margin ?? new Thickness(0), SnapsToDevicePixels = true };
            b.SetResourceReference(Border.BackgroundProperty, brush);
            return b;
        }

        public static Button Button(object content, string style, Action onClick, string tip = null)
        {
            var b = new Button { Content = content };
            b.SetResourceReference(FrameworkElement.StyleProperty, style);
            if (onClick != null) b.Click += (s, e) => onClick();
            if (tip != null) b.ToolTip = tip;
            return b;
        }

        public static Button Link(string text, Action onClick, string tip = null) => Button(text, "Link", onClick, tip);

        /// <summary>MINOR = outlined, HEAVY = solid signal orange.</summary>
        public static Border Tag(Level level)
        {
            bool heavy = level == Level.Heavy;
            var t = Mono(heavy ? "HEAVY" : "MINOR", 10, heavy ? "AccentInk" : "Ink", false);
            t.FontWeight = FontWeights.Bold;
            var b = new Border
            {
                Child = t,
                Padding = new Thickness(6, 2, 6, 2),
                CornerRadius = new CornerRadius(1.5),
                BorderThickness = new Thickness(heavy ? 0 : 1),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                SnapsToDevicePixels = true,
                ToolTip = heavy
                    ? "Heavy: changes a lot. It switches a Windows feature off, removes something, or lowers a protection. Read what it does first."
                    : "Minor: a small, targeted change. Fully reversible, and no feature disappears.",
            };
            if (heavy) b.SetResourceReference(Border.BackgroundProperty, "Accent");
            else b.SetResourceReference(Border.BorderBrushProperty, "Ink");
            return b;
        }

        /// <summary>A small filled square with a check mark (used for "already active").</summary>
        public static FrameworkElement ActiveMark()
        {
            var g = new Grid { Width = 18, Height = 18, Margin = new Thickness(1) };
            var sq = new Border { CornerRadius = new CornerRadius(2) };
            sq.SetResourceReference(Border.BackgroundProperty, "Good");
            var p = new Path
            {
                Data = Geometry.Parse("M 4.5,9.5 L 7.8,12.6 L 13.6,5.6"),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Square,
                StrokeEndLineCap = PenLineCap.Square,
            };
            p.SetResourceReference(Shape.StrokeProperty, "Bg");
            g.Children.Add(sq);
            g.Children.Add(p);
            return g;
        }

        /// <summary>The Simplemize mark: a fader turned all the way down.</summary>
        public static FrameworkElement Logo(double size)
        {
            var c = new Canvas { Width = size, Height = size };
            double r = size * 0.19;
            var bg = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(r) };
            bg.SetResourceReference(Border.BackgroundProperty, "Ink");
            c.Children.Add(bg);
            var track = new Border { Width = size * 0.58, Height = Math.Max(1.5, size * 0.07), CornerRadius = new CornerRadius(size * 0.035) };
            track.SetResourceReference(Border.BackgroundProperty, "Bg");
            Canvas.SetLeft(track, size * 0.21);
            Canvas.SetTop(track, size * 0.5 - track.Height / 2);
            c.Children.Add(track);
            var knob = new Border { Width = size * 0.26, Height = size * 0.26, CornerRadius = new CornerRadius(size * 0.04) };
            knob.SetResourceReference(Border.BackgroundProperty, "Accent");
            Canvas.SetLeft(knob, size * 0.19);
            Canvas.SetTop(knob, size * 0.37);
            c.Children.Add(knob);
            return c;
        }

        public static StackPanel Row(params UIElement[] children)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var c in children) sp.Children.Add(c);
            return sp;
        }

        public static StackPanel Col(params UIElement[] children)
        {
            var sp = new StackPanel();
            foreach (var c in children) sp.Children.Add(c);
            return sp;
        }

        public static T Margin<T>(this T el, double l, double t, double r, double b) where T : FrameworkElement
        {
            el.Margin = new Thickness(l, t, r, b);
            return el;
        }

        public static Border Box(UIElement child, string bg = "Panel", string border = "Line", double pad = 16)
        {
            var b = new Border { Child = child, Padding = new Thickness(pad), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) };
            b.SetResourceReference(Border.BackgroundProperty, bg);
            b.SetResourceReference(Border.BorderBrushProperty, border);
            return b;
        }

        /// <summary>Numbered step: big mono number + text.</summary>
        public static FrameworkElement Step(string number, UIElement body)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var n = Mono(number, 12, "Accent", false);
            n.FontWeight = FontWeights.Bold;
            n.Margin = new Thickness(0, 2, 0, 0);
            g.Children.Add(n);
            Grid.SetColumn(body, 1);
            g.Children.Add(body);
            return g;
        }

        public static FrameworkElement Step(string number, string text) => Step(number, Text(text, 13.5, "Ink"));

        /// <summary>A note with a coloured bar on the left (warnings, hints).</summary>
        public static Border Note(string text, string brush = "Warn", double size = 12.5)
        {
            var t = Text(text, size, brush);
            var b = new Border { Child = t, BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(10, 1, 0, 2) };
            b.SetResourceReference(Border.BorderBrushProperty, brush);
            return b;
        }
    }
}
