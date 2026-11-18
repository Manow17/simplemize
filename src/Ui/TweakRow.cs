using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace Simplemize.Ui
{
    /// <summary>One line in a section page: checkbox, title, plain explanation, tags, and the expandable "what it changes" sheet.</summary>
    public sealed class TweakRow
    {
        public readonly Tweak T;
        public readonly Border Root;
        readonly CheckBox box;
        readonly FrameworkElement activeMark;
        readonly TextBlock title, stateText, sizeText, unavailableText;
        readonly Border warningNote, details;
        readonly Button detailsLink, undoLink;
        readonly StackPanel meta;
        readonly Action<TweakRow, bool> onToggle;

        public TState State = TState.Unknown;
        public string Unavailable;
        public string Warning;
        public bool HasBackup;
        public long Size = -2;   // -2 = not measured yet

        bool suppress;

        public TweakRow(Tweak t, Action<TweakRow, bool> onToggle, Action<TweakRow> onUndo)
        {
            T = t;
            this.onToggle = onToggle;

            Root = new Border
            {
                Padding = new Thickness(14, 16, 14, 16),
                Margin = new Thickness(-14, 0, -14, 0),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Background = System.Windows.Media.Brushes.Transparent,
                SnapsToDevicePixels = true,
            };
            Root.SetResourceReference(Border.BorderBrushProperty, "Line");
            Root.MouseEnter += (s, e) => Root.SetResourceReference(Border.BackgroundProperty, "Hover");
            Root.MouseLeave += (s, e) => Root.Background = System.Windows.Media.Brushes.Transparent;

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Root.Child = g;

            // col 0 — checkbox / active mark
            var c0 = new Grid { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0) };
            box = new CheckBox();
            box.SetResourceReference(FrameworkElement.StyleProperty, "Check");
            box.Checked += (s, e) => { if (!suppress) onToggle(this, true); };
            box.Unchecked += (s, e) => { if (!suppress) onToggle(this, false); };
            AutomationName(box, t.Title);
            activeMark = Kit.ActiveMark();
            activeMark.Visibility = Visibility.Collapsed;
            activeMark.ToolTip = "Already active on this PC.";
            c0.Children.Add(box);
            c0.Children.Add(activeMark);
            g.Children.Add(c0);

            // col 1 — text
            var body = new StackPanel();
            Grid.SetColumn(body, 1);
            g.Children.Add(body);

            title = Kit.Text(t.Title, 15.5, "Ink", "FontDisplay");
            title.Cursor = Cursors.Hand;
            title.MouseLeftButtonUp += (s, e) => { if (box.IsEnabled && box.Visibility == Visibility.Visible) box.IsChecked = !(box.IsChecked == true); };
            body.Children.Add(title);

            var summary = Kit.Text(t.Summary, 13, "Ink2");
            summary.Margin = new Thickness(0, 4, 24, 0);
            summary.MaxWidth = 640;
            summary.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(summary);

            warningNote = Kit.Note("", "Warn");
            warningNote.Margin = new Thickness(0, 10, 24, 0);
            warningNote.Visibility = Visibility.Collapsed;
            warningNote.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(warningNote);

            meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            if (t.Affects.Length > 0)
            {
                meta.Children.Add(Kit.Label("affects").Margin(0, 1, 8, 0));
                meta.Children.Add(Kit.Mono(string.Join(" · ", t.Affects), 11.5, "Ink2", false).Margin(0, 0, 20, 0));
            }
            if (t.After != After.None)
            {
                var a = t.After.HasFlag(After.Restart) ? "restart needed" : t.After.HasFlag(After.SignOut) ? "sign out to finish" : "restarts Explorer";
                meta.Children.Add(Kit.Mono(a, 11.5, "Ink3", false).Margin(0, 0, 20, 0));
            }
            detailsLink = Kit.Link("+ what it changes", ToggleDetails);
            meta.Children.Add(detailsLink);
            undoLink = Kit.Link("undo this", () => onUndo(this), "Put the original values back");
            undoLink.Margin = new Thickness(20, 0, 0, 0);
            undoLink.Visibility = Visibility.Collapsed;
            meta.Children.Add(undoLink);
            body.Children.Add(meta);

            unavailableText = Kit.Mono("", 11.5, "Ink3");
            unavailableText.Margin = new Thickness(0, 8, 0, 0);
            unavailableText.Visibility = Visibility.Collapsed;
            body.Children.Add(unavailableText);

            details = BuildDetails(t);
            details.Visibility = Visibility.Collapsed;
            body.Children.Add(details);

            // col 2 — level tag, state / size
            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 70 };
            Grid.SetColumn(right, 2);
            var tag = Kit.Tag(t.Level);
            tag.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(tag);
            stateText = Kit.Mono("", 10.5, "Good", false);
            stateText.FontWeight = FontWeights.SemiBold;
            stateText.HorizontalAlignment = HorizontalAlignment.Right;
            stateText.Margin = new Thickness(0, 8, 0, 0);
            right.Children.Add(stateText);
            sizeText = Kit.Mono("", 12, "Ink", false);
            sizeText.HorizontalAlignment = HorizontalAlignment.Right;
            sizeText.Margin = new Thickness(0, 8, 0, 0);
            sizeText.Visibility = t.Measure != null ? Visibility.Visible : Visibility.Collapsed;
            right.Children.Add(sizeText);
            g.Children.Add(right);
        }

        static void AutomationName(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);

        public bool Selected
        {
            get => box.IsChecked == true;
            set
            {
                suppress = true;
                box.IsChecked = value;
                suppress = false;
            }
        }

        public bool Selectable => Unavailable == null && State != TState.Active;

        public bool DetailsOpen => details.Visibility == Visibility.Visible;

        public void ToggleDetails()
        {
            bool open = details.Visibility != Visibility.Visible;
            details.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            detailsLink.Content = open ? "− hide details" : "+ what it changes";
        }

        public void Update()
        {
            bool active = State == TState.Active && !T.OneShot;
            box.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            activeMark.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            box.IsEnabled = Selectable;
            Root.Opacity = Unavailable != null ? 0.55 : 1;

            switch (State)
            {
                case TState.Active when !T.OneShot:
                    stateText.Text = "ACTIVE";
                    stateText.SetResourceReference(TextBlock.ForegroundProperty, "Good");
                    break;
                case TState.Partial:
                    stateText.Text = "PARTLY ON";
                    stateText.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
                    stateText.ToolTip = "Some of these values are already set, some aren't. Applying sets the rest.";
                    break;
                default:
                    stateText.Text = "";
                    break;
            }
            stateText.Visibility = stateText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (T.Measure != null)
                sizeText.Text = Size == -2 ? "…" : Size < 0 ? "–" : Fmt.Bytes(Size);

            if (Unavailable != null)
            {
                unavailableText.Text = "Not available: " + Unavailable;
                unavailableText.Visibility = Visibility.Visible;
            }
            else unavailableText.Visibility = Visibility.Collapsed;

            if (Warning != null && Unavailable == null)
            {
                ((TextBlock)warningNote.Child).Text = Warning;
                warningNote.Visibility = Visibility.Visible;
            }
            else warningNote.Visibility = Visibility.Collapsed;

            undoLink.Visibility = HasBackup ? Visibility.Visible : Visibility.Collapsed;
            if (!Selectable && Selected) Selected = false;
        }

        // ---- the "what it changes" sheet ------------------------------------------------

        static Border BuildDetails(Tweak t)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            g.ColumnDefinitions.Add(new ColumnDefinition());

            int row = 0;
            void Add(string label, UIElement value)
            {
                g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var l = Kit.Label(label, "Ink3", 10);
                l.Margin = new Thickness(0, 3, 12, 0);
                Grid.SetRow(l, row);
                g.Children.Add(l);
                if (value is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 0, 14);
                Grid.SetRow(value, row);
                Grid.SetColumn(value, 1);
                g.Children.Add(value);
                row++;
            }

            if (!string.IsNullOrEmpty(t.Gain)) Add("You get", Kit.Text(t.Gain, 13, "Ink"));
            if (!string.IsNullOrEmpty(t.TradeOff)) Add("Trade-off", Kit.Text(t.TradeOff, 13, "Ink"));

            var changes = new StackPanel();
            foreach (var a in t.Actions)
            {
                var d = a.Describe();
                var w = Kit.Mono(d.Where, 11, "Ink3");
                var x = Kit.Mono(d.What, 11.5, "Ink");
                x.Margin = new Thickness(0, 1, 0, 8);
                changes.Children.Add(w);
                changes.Children.Add(x);
            }
            Add("Changes", changes);

            var undo = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            undo.SetResourceReference(TextBlock.FontFamilyProperty, "FontBody");
            undo.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            if (!t.Revertible && !t.OneShot) undo.Inlines.Add(Kit.Run("Not undoable by Simplemize. ", "Bad", FontWeights.SemiBold));
            undo.Inlines.Add(new Run(t.UndoText.StartsWith("Simplemize can't") && !t.Revertible && !t.OneShot
                ? t.UndoText.Substring(t.UndoText.IndexOf('.') + 1).Trim()
                : t.UndoText));
            Add("Undo", undo);

            var box = Kit.Box(g, "Panel", "Line", 16);
            box.Margin = new Thickness(0, 14, 24, 2);
            box.Padding = new Thickness(18, 16, 18, 4);
            return box;
        }
    }
}
