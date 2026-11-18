using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Simplemize.Ui
{
    public sealed partial class MainWindow
    {
        // ---- overlay plumbing ----------------------------------------------------------

        Border Card(double width)
        {
            var b = new Border
            {
                Width = width,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(40),
            };
            b.SetResourceReference(Border.BackgroundProperty, "Panel");
            b.SetResourceReference(Border.BorderBrushProperty, "Ink");
            return b;
        }

        void ShowOverlay(Border card)
        {
            while (overlay.Children.Count > 1) overlay.Children.RemoveAt(1);
            overlay.Children.Add(card);
            overlay.Visibility = Visibility.Visible;
        }

        void HideOverlay()
        {
            overlay.Visibility = Visibility.Collapsed;
            while (overlay.Children.Count > 1) overlay.Children.RemoveAt(1);
        }

        static DockPanel CardLayout(FrameworkElement top, FrameworkElement body, FrameworkElement footer)
        {
            var d = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(top, Dock.Top);
            d.Children.Add(top);
            if (footer != null)
            {
                var f = new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(30, 16, 24, 18) };
                f.SetResourceReference(Border.BorderBrushProperty, "Line");
                DockPanel.SetDock(f, Dock.Bottom);
                d.Children.Add(f);
            }
            d.Children.Add(body);
            return d;
        }

        static FrameworkElement CardTitle(string eyebrow, string title, string sub)
        {
            var sp = new StackPanel { Margin = new Thickness(30, 26, 30, 0) };
            sp.Children.Add(Kit.Label(eyebrow, "Accent", 10.5));
            sp.Children.Add(Kit.Display(title, 27).Margin(-1, 8, 0, 0));
            if (sub != null) sp.Children.Add(Kit.Text(sub, 13.5, "Ink2").Margin(0, 6, 0, 0));
            return sp;
        }

        static Grid FooterGrid(FrameworkElement left, params Button[] buttons)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (left != null)
            {
                left.VerticalAlignment = VerticalAlignment.Center;
                left.Margin = new Thickness(0, 0, 20, 0);
                g.Children.Add(left);
            }
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < buttons.Length; i++)
            {
                if (i > 0) buttons[i].Margin = new Thickness(10, 0, 0, 0);
                sp.Children.Add(buttons[i]);
            }
            Grid.SetColumn(sp, 1);
            g.Children.Add(sp);
            return g;
        }

        double CardMaxHeight => Math.Max(420, (ActualHeight > 0 ? ActualHeight : Height) - 90);

        // ---- review ----------------------------------------------------------------------

        public void OpenReview()
        {
            var sel = SelectedTweaks();
            if (sel.Count == 0 || busy) return;
            int heavy = sel.Count(t => t.Level == Level.Heavy);
            var after = sel.Aggregate(After.None, (a, t) => a | t.After);

            var card = Card(680);
            card.MaxHeight = CardMaxHeight;

            var top = new StackPanel();
            top.Children.Add(CardTitle("Review before applying",
                sel.Count + (sel.Count == 1 ? " change" : " changes"),
                heavy == 0 ? "All of them minor and fully reversible." : heavy + " heavy, " + (sel.Count - heavy) + " minor. Heavy changes are listed first."));

            // restore point promise
            var rp = new Grid { Margin = new Thickness(30, 20, 30, 6) };
            rp.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            rp.ColumnDefinitions.Add(new ColumnDefinition());
            rp.Children.Add(Kit.ActiveMark());
            var rpText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 20 };
            rpText.SetResourceReference(TextBlock.FontFamilyProperty, "FontBody");
            rpText.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            if (engine.SessionRestorePoint == null)
            {
                rpText.Inlines.Add(Kit.Run("First, a restore point named “" + Engine.RestorePointName + "” is created. ", "Ink", FontWeights.SemiBold));
                rpText.Inlines.Add(Kit.Run("If System Restore is off for your system drive, Simplemize turns it on for this. If anything feels wrong later, rewind with System Restore or undo single changes on the Undo page.", "Ink2"));
            }
            else
            {
                rpText.Inlines.Add(Kit.Run("This session's restore point already exists. ", "Ink", FontWeights.SemiBold));
                rpText.Inlines.Add(Kit.Run("Original values are saved for undo as always.", "Ink2"));
            }
            Grid.SetColumn(rpText, 1);
            rp.Children.Add(rpText);
            top.Children.Add(rp);

            // list
            var list = new StackPanel { Margin = new Thickness(30, 4, 30, 22) };
            void Group(string label, IEnumerable<Tweak> items)
            {
                var arr = items.ToList();
                if (arr.Count == 0) return;
                var h = Kit.Label(label + "  " + arr.Count, "Ink", 10.5);
                h.Margin = new Thickness(0, 18, 0, 6);
                list.Children.Add(h);
                list.Children.Add(Kit.Rule(1.5, "Ink"));
                foreach (var t in arr)
                {
                    var r = rows[t.Id];
                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var left = new StackPanel();
                    left.Children.Add(Kit.Text(t.Title, 14, "Ink", "FontDisplay"));
                    var flags = new List<(string text, string brush)>();
                    if (!t.Revertible && !t.OneShot) flags.Add(("not undoable by Simplemize", "Bad"));
                    if (t.Security) flags.Add(("lowers a security protection", "Bad"));
                    if (t.Level == Level.Heavy && t.Revertible) flags.Add(("reversible", "Ink3"));
                    if (t.After.HasFlag(After.Restart)) flags.Add(("restart needed", "Ink3"));
                    if (t.Slow) flags.Add(("takes several minutes", "Ink3"));
                    if (r.Warning != null) flags.Add((r.Warning, "Warn"));
                    if (flags.Count > 0)
                    {
                        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 3, 0, 0) };
                        tb.SetResourceReference(TextBlock.FontFamilyProperty, "FontMono");
                        for (int i = 0; i < flags.Count; i++)
                        {
                            if (i > 0) tb.Inlines.Add(Kit.Run("  ·  ", "Ink3"));
                            tb.Inlines.Add(Kit.Run(flags[i].text, flags[i].brush));
                        }
                        left.Children.Add(tb);
                    }
                    g.Children.Add(left);
                    var right = Kit.Mono(t.Measure != null && r.Size >= 0 ? "~" + Fmt.Bytes(r.Size) : t.Section.ToString().ToLowerInvariant(), 11, "Ink3", false);
                    right.Margin = new Thickness(16, 2, 0, 0);
                    Grid.SetColumn(right, 1);
                    g.Children.Add(right);
                    var row = new Border { Child = g, Padding = new Thickness(0, 10, 0, 10), BorderThickness = new Thickness(0, 0, 0, 1) };
                    row.SetResourceReference(Border.BorderBrushProperty, "Line");
                    list.Children.Add(row);
                }
            }
            Group("Heavy", sel.Where(t => t.Level == Level.Heavy && !t.OneShot));
            Group("Minor", sel.Where(t => t.Level == Level.Minor && !t.OneShot));
            Group("One-time cleanup", sel.Where(t => t.OneShot));

            var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };

            string afterText = after.HasFlag(After.Restart) ? "You'll need to restart afterwards."
                : after.HasFlag(After.SignOut) ? "Sign out afterwards to finish."
                : after.HasFlag(After.Explorer) ? "Explorer restarts briefly to show some changes."
                : "Takes effect right away.";
            var cancel = Kit.Button("Cancel", "Secondary", HideOverlay);
            var go = Kit.Button(engine.SessionRestorePoint == null ? "Create restore point & apply" : "Apply", "Primary", () => RunApply(sel));
            go.IsEnabled = CanChange;
            card.Child = CardLayout(top, scroll, FooterGrid(Kit.Mono(afterText, 11, "Ink2"), cancel, go));
            ShowOverlay(card);
            go.Focus();
        }

        // ---- progress ------------------------------------------------------------------

        sealed class StepLine
        {
            public TextBlock Status, Title, Detail;
        }

        async void RunApply(List<Tweak> sel)
        {
            if (busy) return;
            busy = true;
            UpdateBar();

            var card = Card(680);
            card.MaxHeight = CardMaxHeight;
            var top = new StackPanel();
            var titleBlock = CardTitle("Applying", "Working on it…", "Keep Simplemize open until it's done.");
            top.Children.Add(titleBlock);
            var track = new Grid { Height = 3, Margin = new Thickness(30, 20, 30, 0) };
            var trackBg = new Border();
            trackBg.SetResourceReference(Border.BackgroundProperty, "Line");
            var fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            fill.SetResourceReference(Border.BackgroundProperty, "Accent");
            track.Children.Add(trackBg);
            track.Children.Add(fill);
            top.Children.Add(track);

            var list = new StackPanel { Margin = new Thickness(30, 14, 30, 20) };
            var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            var footerLeft = new ContentControl { Focusable = false };
            var done = Kit.Button("Done", "Primary", () => { HideOverlay(); });
            done.IsEnabled = false;
            var footerButtons = new StackPanel { Orientation = Orientation.Horizontal };
            footerButtons.Children.Add(done);
            var footer = new Grid();
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.Children.Add(footerLeft);
            Grid.SetColumn(footerButtons, 1);
            footer.Children.Add(footerButtons);
            card.Child = CardLayout(top, scroll, footer);
            ShowOverlay(card);

            var lines = new Dictionary<int, StepLine>();
            void OnStep(Step s)
            {
                StepLine line = null;
                if (s.Kind != StepKind.Info) lines.TryGetValue(s.Index, out line);
                if (line == null || s.Kind == StepKind.Running && s.Index >= 0 && line.Status.Text != "··")
                {
                    var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    line = new StepLine { Status = Kit.Mono("", 11, "Ink3", false), Title = Kit.Text("", 13.5, "Ink"), Detail = Kit.Mono("", 11, "Ink2") };
                    line.Status.FontWeight = FontWeights.Bold;
                    line.Status.Margin = new Thickness(0, 2, 0, 0);
                    g.Children.Add(line.Status);
                    var body = Kit.Col(line.Title, line.Detail);
                    Grid.SetColumn(body, 1);
                    g.Children.Add(body);
                    list.Children.Add(g);
                    if (s.Kind != StepKind.Info) lines[s.Index] = line;
                }
                line.Title.Text = s.Title;
                line.Detail.Text = s.Detail ?? "";
                line.Detail.Visibility = string.IsNullOrEmpty(s.Detail) ? Visibility.Collapsed : Visibility.Visible;
                string status, brush;
                switch (s.Kind)
                {
                    case StepKind.Running: status = "··"; brush = "Ink3"; break;
                    case StepKind.Ok: status = "OK"; brush = "Good"; break;
                    case StepKind.Partial: status = "PART"; brush = "Warn"; break;
                    case StepKind.Fail: status = "FAIL"; brush = "Bad"; break;
                    case StepKind.Skip: status = "SKIP"; brush = "Ink3"; break;
                    default: status = "NOTE"; brush = "Ink2"; break;
                }
                line.Status.Text = status;
                line.Status.SetResourceReference(TextBlock.ForegroundProperty, brush);
                if (s.Total > 0 && s.Kind != StepKind.Running)
                    fill.Width = Math.Max(0, (track.ActualWidth > 0 ? track.ActualWidth : 616) * (s.Index + 1) / s.Total);
                scroll.ScrollToEnd();
            }

            BatchResult result;
            try
            {
                result = await Task.Run(() => engine.Apply(sel,
                    s => Dispatcher.Invoke(() => OnStep(s)),
                    msg => Dispatcher.Invoke(() => AskNoRestorePoint(msg)).Result));
            }
            catch (Exception ex)
            {
                env.Log.Error("apply crashed: " + ex);
                result = new BatchResult { Failed = sel.Count };
                OnStep(new Step { Kind = StepKind.Fail, Index = -2, Title = "Something went wrong", Detail = ex.Message });
            }

            busy = false;
            if (result.Cancelled)
            {
                HideOverlay();
                Toast("Nothing was changed.");
                UpdateBar();
                return;
            }

            fill.Width = track.ActualWidth;
            foreach (var t in sel) if (rows.TryGetValue(t.Id, out var r)) r.Selected = false;
            selected.Clear();
            await RefreshStatesAsync(true);

            // finish screen
            var tb = (StackPanel)titleBlock;
            ((TextBlock)tb.Children[0]).Text = "DONE";
            ((TextBlock)tb.Children[1]).Text = result.Failed == 0 ? "All set." : "Done, with a few problems.";
            ((TextBlock)tb.Children[2]).Text = result.Applied + " applied  ·  " + result.Skipped + " skipped  ·  " + result.Failed + " with problems";

            var notes = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            if (result.After.HasFlag(After.Restart))
                notes.Children.Add(Kit.Note("Restart your PC to finish: some changes only take effect after a restart.", "Accent", 13).Margin(0, 8, 0, 0));
            else if (result.After.HasFlag(After.SignOut))
                notes.Children.Add(Kit.Note("Sign out and back in to finish the animation changes.", "Accent", 13).Margin(0, 8, 0, 0));
            if (result.After.HasFlag(After.Explorer))
                notes.Children.Add(Kit.Note("Some changes show up once Explorer (the taskbar and desktop) restarts. It takes two seconds; open File Explorer windows close.", "Ink2", 13).Margin(0, 8, 0, 0));
            var back = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 16, 0, 0), LineHeight = 19 };
            back.SetResourceReference(TextBlock.FontFamilyProperty, "FontBody");
            back.SetResourceReference(TextBlock.ForegroundProperty, "Ink2");
            back.Inlines.Add(Kit.Run("Changed your mind? ", "Ink", FontWeights.SemiBold));
            back.Inlines.Add(Kit.Run("Undo single changes on the Undo page, or rewind everything with System Restore: press Win + R, type rstrui, and pick “" + Engine.RestorePointName + "”."));
            if (result.RestorePointFailed) back.Inlines.Add(Kit.Run(" (No restore point was made this time, so use the Undo page.)", "Warn"));
            notes.Children.Add(back);
            list.Children.Add(notes);
            scroll.ScrollToEnd();

            footerButtons.Children.Clear();
            if (result.After.HasFlag(After.Explorer))
            {
                var ex = Kit.Button("Restart Explorer", "Secondary", async () =>
                {
                    await Task.Run(() => env.Sys.RestartExplorer());
                    Toast(env.Sandbox ? "Sandbox: Explorer was not restarted." : "Explorer restarted.");
                });
                ex.Margin = new Thickness(0, 0, 10, 0);
                footerButtons.Children.Add(ex);
            }
            if (result.After.HasFlag(After.Restart))
            {
                var rs = Kit.Button("Restart now", "Secondary", RestartPc);
                rs.Margin = new Thickness(0, 0, 10, 0);
                footerButtons.Children.Add(rs);
            }
            var undoPage = Kit.Link("go to Undo page", () => { HideOverlay(); ShowPage("undo"); });
            footerLeft.Content = undoPage;
            footerButtons.Children.Add(done);
            done.IsEnabled = true;
            done.Focus();
            UpdateBar();
        }

        // ---- hooks for --render ---------------------------------------------------------

        public void ExpandForShot(string id) => rows[id].ToggleDetails();
        public void ConfirmReviewForShot() => RunApply(SelectedTweaks());
        public bool IsBusyForShot => busy;
        public void CloseOverlayForShot() => HideOverlay();

        void RestartPc()
        {
            if (env.Sandbox) { Toast("Sandbox: the PC was not restarted."); return; }
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                    "/r /t 10 /c \"Simplemize: restarting to finish applying changes.\"") { UseShellExecute = false, CreateNoWindow = true });
                Toast("Restarting in 10 seconds. Save anything you have open.");
            }
            catch (Exception ex) { Toast("Couldn't restart: " + ex.Message, "Bad"); }
        }

        // ---- question: continue without a restore point? ------------------------------

        Task<bool> AskNoRestorePoint(string reason)
        {
            var tcs = new TaskCompletionSource<bool>();
            var previous = overlay.Children.Count > 1 ? overlay.Children[1] : null;

            var card = Card(560);
            var top = CardTitle("No restore point", "Windows couldn't make a restore point", null);
            var body = new StackPanel { Margin = new Thickness(30, 12, 30, 24) };
            body.Children.Add(Kit.Mono(reason ?? "", 11.5, "Bad").Margin(0, 0, 0, 14));
            body.Children.Add(Kit.Text("You can stop here and nothing changes. Or you can go on: Simplemize still saves every original value, so the reversible changes can be undone from the Undo page. Removed apps and cleanups can't be undone either way.", 13.5, "Ink"));

            void Finish(bool go)
            {
                overlay.Children.Remove(overlay.Children.Count > 1 ? overlay.Children[overlay.Children.Count - 1] : null);
                if (previous != null && !overlay.Children.Contains(previous)) overlay.Children.Add(previous);
                tcs.TrySetResult(go);
            }
            var stop = Kit.Button("Stop, change nothing", "Primary", () => Finish(false));
            var cont = Kit.Button("Continue without it", "Secondary", () => Finish(true));
            card.Child = CardLayout(top, body, FooterGrid(null, cont, stop));
            if (previous != null) overlay.Children.Remove(previous);
            overlay.Children.Add(card);
            overlay.Visibility = Visibility.Visible;
            stop.Focus();
            return tcs.Task;
        }
    }
}
