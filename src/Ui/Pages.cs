using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace Simplemize.Ui
{
    public sealed partial class MainWindow
    {
        // ---- shared page pieces --------------------------------------------------------

        static ScrollViewer PageScroll(FrameworkElement content)
        {
            // A star column with a max width: content is as wide as the window allows, up to 900, always left-aligned.
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 900 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(content);
            var holder = new Border { Padding = new Thickness(48, 2, 44, 64), Child = grid };
            return new ScrollViewer
            {
                Content = holder,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                PanningMode = PanningMode.VerticalOnly,
            };
        }

        static FrameworkElement Header(string num, string eyebrow, string title, string lede)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var eb = new TextBlock { FontSize = 11.5 };
            eb.SetResourceReference(TextBlock.FontFamilyProperty, "FontMono");
            eb.Inlines.Add(Kit.Run(num, "Accent", FontWeights.Bold));
            eb.Inlines.Add(Kit.Run("  /  " + eyebrow.ToUpperInvariant(), "Ink3", FontWeights.SemiBold));
            sp.Children.Add(eb);
            var t = Kit.Display(title, 38);
            t.Margin = new Thickness(-1, 10, 0, 0);
            sp.Children.Add(t);
            if (!string.IsNullOrEmpty(lede))
            {
                var l = Kit.Text(lede, 14.5, "Ink2");
                l.Margin = new Thickness(0, 10, 0, 0);
                l.MaxWidth = 680;
                l.HorizontalAlignment = HorizontalAlignment.Left;
                l.LineHeight = 22;
                sp.Children.Add(l);
            }
            return sp;
        }

        /// <param name="bleed">Extend the rule 14 px on both sides, to line up with tweak rows (which bleed for their hover background).</param>
        static FrameworkElement GroupHeader(string name, out TextBlock count, double top = 40, bool bleed = false)
        {
            double b = bleed ? 14 : 0;
            var sp = new StackPanel { Margin = new Thickness(-b, top, -b, 0) };
            var g = new Grid { Margin = new Thickness(b, 0, b, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(Kit.Label(name, "Ink", 11));
            count = Kit.Mono("", 11, "Ink3", false);
            Grid.SetColumn(count, 1);
            g.Children.Add(count);
            sp.Children.Add(g);
            sp.Children.Add(Kit.Rule(1.5, "Ink"));
            return sp;
        }

        // ---- section pages -------------------------------------------------------------

        FrameworkElement BuildSectionPage(PageInfo info)
        {
            var sec = info.Section.Value;
            var view = new SectionView { Section = sec };
            sectionViews[info.Key] = view;
            var items = tweaks.Where(t => t.Section == sec).ToList();

            var content = new StackPanel();
            content.Children.Add(Header(info.Num, info.Label, info.Title, info.Lede));

            // toolbar: level filter + bulk links
            var bar = new Grid { Margin = new Thickness(0, 28, 0, 0) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition());
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var seg = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var f in new[] { "all", "minor", "heavy" })
            {
                int n = f == "all" ? items.Count : items.Count(t => t.Level == (f == "minor" ? Level.Minor : Level.Heavy));
                var rb = new RadioButton { Content = (f == "all" ? "All" : f == "minor" ? "Minor" : "Heavy") + "  " + n, GroupName = "filter-" + info.Key };
                rb.SetResourceReference(StyleProperty, "Seg");
                var key = f;
                rb.Checked += (s, e) => { filter = key; ApplyFilter(view); };
                view.Filters[f] = rb;
                seg.Children.Add(rb);
            }
            bar.Children.Add(seg);
            var bulk = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var selMinor = Kit.Link("select every minor change", () =>
            {
                foreach (var t in items.Where(x => x.Level == Level.Minor)) SetSelected(t, true);
                UpdateBar();
            });
            var clearPage = Kit.Link("clear this page", () =>
            {
                foreach (var t in items) SetSelected(t, false);
                UpdateBar();
            });
            clearPage.Margin = new Thickness(22, 0, 0, 0);
            bulk.Children.Add(selMinor);
            bulk.Children.Add(clearPage);
            Grid.SetColumn(bulk, 2);
            bar.Children.Add(bulk);
            content.Children.Add(bar);

            bool first = true;
            foreach (var grp in items.GroupBy(t => t.Group))
            {
                var gv = new GroupView { Name = grp.Key, Panel = new StackPanel(), RowsHost = new StackPanel() };
                gv.Panel.Children.Add(GroupHeader(grp.Key, out gv.Count, first ? 30 : 46, bleed: true));
                first = false;
                if (grp.Key == "Preinstalled apps")
                {
                    var note = Kit.Text("Tick the apps you want gone. Simplemize can't reinstall them, but each one is free in the Microsoft Store. Apps that aren't installed on this PC aren't listed.", 13, "Ink2");
                    note.Margin = new Thickness(0, 12, 0, 4);
                    note.MaxWidth = 680;
                    note.HorizontalAlignment = HorizontalAlignment.Left;
                    var suggest = Kit.Link("select the usual suspects", () =>
                    {
                        foreach (var t in grp.Where(x => x.Presets.HasFlag(Preset.Stripped))) SetSelected(t, true);
                        UpdateBar();
                    }, "Selects the apps in the Stripped preset: Microsoft promos and sponsored games. Streaming and social apps are left for you to decide.");
                    suggest.Margin = new Thickness(0, 4, 0, 8);
                    suggest.HorizontalAlignment = HorizontalAlignment.Left;
                    gv.Panel.Children.Add(note);
                    gv.Panel.Children.Add(suggest);
                    gv.Placeholder = Kit.Mono("Checking which apps are installed…", 12, "Ink3").Margin(0, 16, 0, 16);
                    gv.Panel.Children.Add(gv.Placeholder);
                }
                gv.Panel.Children.Add(gv.RowsHost);
                foreach (var t in grp)
                {
                    var r = rows[t.Id];
                    gv.Rows.Add(r);
                    if (r.Root.Parent is Panel old) old.Children.Remove(r.Root);
                    gv.RowsHost.Children.Add(r.Root);
                }
                view.Groups.Add(gv);
                content.Children.Add(gv.Panel);
            }

            if (sec == Section.Performance)
            {
                var honest = Kit.Note("Straight talk: no settings tweak doubles your FPS. What these do is remove the things that get in a game's way: power saving, background work, recording hooks and scheduling defaults built for office PCs. The difference is largest on PCs that were never tuned.", "Ink3", 12.5);
                honest.Margin = new Thickness(0, 36, 0, 0);
                honest.MaxWidth = 680;
                honest.HorizontalAlignment = HorizontalAlignment.Left;
                content.Children.Add(honest);
            }

            RebuildApps(view);
            return PageScroll(content);
        }

        void RebuildApps(SectionView view)
        {
            foreach (var gv in view.Groups.Where(g => g.Placeholder != null))
                gv.Placeholder.Visibility = env.Apps == null ? Visibility.Visible : Visibility.Collapsed;
        }

        void ApplyFilter(SectionView view)
        {
            if (view.Filters.TryGetValue(filter, out var rb) && rb.IsChecked != true) rb.IsChecked = true;
            foreach (var gv in view.Groups)
            {
                int shown = 0;
                foreach (var r in gv.Rows)
                {
                    bool isApp = r.T.Group == "Preinstalled apps";
                    bool hidden = (filter == "minor" && r.T.Level != Level.Minor)
                                  || (filter == "heavy" && r.T.Level != Level.Heavy)
                                  || (isApp && (env.Apps == null || r.Unavailable != null));
                    r.Root.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
                    if (!hidden) shown++;
                }
                bool isApps = gv.Placeholder != null;
                gv.Panel.Visibility = shown > 0 || (isApps && env.Apps == null && filter != "minor") ? Visibility.Visible : Visibility.Collapsed;
                gv.Count.Text = isApps && env.Apps != null ? shown + " installed" : shown.ToString();
            }

            // filter labels count what can actually be shown (apps that aren't installed don't count)
            var countable = view.Groups.SelectMany(g => g.Rows)
                .Where(r => r.T.Group != "Preinstalled apps" || (env.Apps != null && r.Unavailable == null)).ToList();
            foreach (var kv in view.Filters)
            {
                int n = kv.Key == "all" ? countable.Count : countable.Count(r => r.T.Level == (kv.Key == "minor" ? Level.Minor : Level.Heavy));
                kv.Value.Content = (kv.Key == "all" ? "All" : kv.Key == "minor" ? "Minor" : "Heavy") + "  " + n;
            }
        }

        // ---- overview ------------------------------------------------------------------

        FrameworkElement BuildOverview()
        {
            var c = new StackPanel();
            string hello = DateTime.Now.Hour < 12 ? "Good morning." : DateTime.Now.Hour < 18 ? "Good afternoon." : "Good evening.";
            c.Children.Add(Header("01", "Overview", hello + " Here's your PC right now.",
                "Simplemize switches off the parts of Windows you never asked for. Start with a preset or go through the pages on the left. Nothing changes until you review the list and press Apply."));

            // live numbers
            var snap = lastSnap;
            var stats = new Grid();
            for (int i = 0; i < 4; i++) stats.ColumnDefinitions.Add(new ColumnDefinition());
            var baseSnap = engine.Backup.Baseline;
            string startupCount = "–";
            try { startupCount = env.Demo ? "6" : Startup.CountEnabled(env.Reg).ToString(); } catch { }
            AddStat(stats, 0, "Processes", snap?.Processes.ToString() ?? "…", Delta(baseSnap?.Processes, snap?.Processes));
            AddStat(stats, 1, "Services running", snap?.Services.ToString() ?? "…", Delta(baseSnap?.Services, snap?.Services));
            AddStat(stats, 2, "Startup apps on", startupCount, null);
            AddStat(stats, 3, "Memory in use", snap == null ? "…" : (snap.RamUsed / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
                    snap == null ? null : "of " + Math.Round(snap.RamTotal / 1073741824.0) + " GB");
            c.Children.Add(Kit.Rule(1.5, "Ink", new Thickness(0, 34, 0, 0)));
            c.Children.Add(stats);
            c.Children.Add(Kit.Rule(1, "Line", new Thickness(0, 18, 0, 0)));
            var foot = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            foot.ColumnDefinitions.Add(new ColumnDefinition());
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foot.Children.Add(Kit.Mono(baseSnap != null
                ? "Compared with the first time you applied changes (" + ShortDate(baseSnap.At) + "). Numbers drop most after a restart."
                : "Measured live. Apply changes and restart to see these drop.", 11, "Ink3"));
            var refresh = Kit.Link("refresh", async () =>
            {
                lastSnap = await Task.Run(() => Snapshot.Take());
                if (page == "overview") ShowPage("overview");
            });
            Grid.SetColumn(refresh, 1);
            foot.Children.Add(refresh);
            c.Children.Add(foot);
            if (snap == null && !renderMode)
                Task.Run(() => Snapshot.Take()).ContinueWith(t =>
                {
                    lastSnap = t.Result;
                    Dispatcher.Invoke(() => { if (page == "overview") ShowPage("overview"); });
                });

            // presets
            c.Children.Add(GroupHeader("Start with a preset", out _, 52));
            AddPreset(c, Preset.Light, "Light touch",
                "Minor changes only: privacy, ads, small speed-ups and a quick cleanup. Nothing is removed and no feature is switched off for good.");
            AddPreset(c, Preset.Gamer, "Gamer",
                "Light touch plus the power, scheduler, input and network tweaks that matter for input delay and steady FPS.");
            AddPreset(c, Preset.Stripped, "Stripped",
                "Gamer plus heavy debloat: removes preinstalled apps, trims services and background activity. Read the review list carefully.");
            var pnote = Kit.Mono("Presets skip anything that doesn't suit this PC (battery-hungry tweaks on a laptop, for example) and anything already active. You can change every pick before applying.", 11, "Ink3");
            pnote.Margin = new Thickness(0, 14, 0, 0);
            c.Children.Add(pnote);

            // minor vs heavy
            c.Children.Add(GroupHeader("Minor and heavy", out _, 52));
            var mh = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            mh.ColumnDefinitions.Add(new ColumnDefinition());
            mh.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            mh.ColumnDefinitions.Add(new ColumnDefinition());
            var minor = Kit.Col(Kit.Tag(Level.Minor), Kit.Text("A small, targeted change. Fully reversible, and no feature disappears. Safe to apply without much thought.", 13.5, "Ink").Margin(0, 10, 0, 0));
            var heavy = Kit.Col(Kit.Tag(Level.Heavy), Kit.Text("Changes a lot: switches a Windows feature off, removes an app, or lowers a protection. Open “+ what it changes” and read the trade-off first.", 13.5, "Ink").Margin(0, 10, 0, 0));
            mh.Children.Add(minor);
            Grid.SetColumn(heavy, 2);
            mh.Children.Add(heavy);
            c.Children.Add(mh);

            // safety net
            c.Children.Add(GroupHeader("Your safety net", out _, 52));
            var steps = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            steps.Children.Add(Kit.Step("1", "Before anything changes, Simplemize creates a System Restore point named “" + Engine.RestorePointName + "”."));
            steps.Children.Add(Kit.Step("2", "It also saves the original value of every setting it touches, so you can undo one change at a time, exactly."));
            steps.Children.Add(Kit.Step("3", "Nothing is applied until you've seen the full list and pressed Apply."));
            c.Children.Add(steps);
            var how = Kit.Link("how to go back →", () => ShowPage("undo"));
            how.HorizontalAlignment = HorizontalAlignment.Left;
            how.Margin = new Thickness(34, 2, 0, 0);
            c.Children.Add(how);
            return PageScroll(c);
        }

        static string ShortDate(string iso)
        {
            return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : iso;
        }

        static string Delta(int? before, int? now)
        {
            if (before == null || now == null) return null;
            int d = now.Value - before.Value;
            return d == 0 ? "same as before" : (d < 0 ? "−" + (-d) + " since first apply" : "+" + d + " since first apply");
        }

        static void AddStat(Grid g, int col, string label, string value, string sub)
        {
            var sp = new StackPanel { Margin = new Thickness(col == 0 ? 0 : 20, 16, 0, 0) };
            sp.Children.Add(Kit.Label(label, "Ink3", 10.5));
            var v = Kit.Display(value, 40, "Ink", FontWeights.Light);
            v.Margin = new Thickness(-2, 6, 0, 0);
            sp.Children.Add(v);
            if (sub != null)
            {
                bool good = sub.StartsWith("−");
                sp.Children.Add(Kit.Mono(sub, 11, good ? "Good" : "Ink3").Margin(0, 2, 0, 0));
            }
            if (col > 0)
            {
                var line = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
                line.SetResourceReference(Border.BackgroundProperty, "Line");
                Grid.SetColumn(line, col);
                g.Children.Add(line);
            }
            Grid.SetColumn(sp, col);
            g.Children.Add(sp);
        }

        void AddPreset(Panel parent, Preset p, string name, string desc)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var n = Kit.Display(name, 18);
            g.Children.Add(n);
            var d = Kit.Text(desc, 13, "Ink2");
            d.Margin = new Thickness(0, 2, 24, 0);
            Grid.SetColumn(d, 1);
            g.Children.Add(d);
            int count = statesLoaded ? CountPreset(p) : -1;
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var cnt = Kit.Mono(count < 0 ? "…" : count + " changes", 11.5, "Ink3", false);
            cnt.VerticalAlignment = VerticalAlignment.Center;
            cnt.Margin = new Thickness(0, 0, 16, 0);
            right.Children.Add(cnt);
            var btn = Kit.Button("Select", "Secondary", () =>
            {
                int k = SelectPreset(p);
                Toast(k + " changes selected from “" + name + "”. Review them on each page, or press Review & apply.");
            });
            btn.Padding = new Thickness(16, 6, 16, 7);
            btn.IsEnabled = statesLoaded;
            right.Children.Add(btn);
            Grid.SetColumn(right, 2);
            g.Children.Add(right);
            var row = new Border { Child = g, Padding = new Thickness(0, 18, 0, 18), BorderThickness = new Thickness(0, 0, 0, 1) };
            row.SetResourceReference(Border.BorderBrushProperty, "Line");
            parent.Children.Add(row);
        }

        // ---- startup apps --------------------------------------------------------------

        FrameworkElement BuildStartupPage()
        {
            var c = new StackPanel();
            c.Children.Add(Header("07", "Startup apps", "What starts with Windows",
                "Apps that launch every time you sign in. Switching one off here works exactly like Task Manager's Startup tab: the app stays installed and you can switch it back on anytime. Changes apply right away."));

            List<StartupItem> items;
            try { items = Startup.List(env.Reg); }
            catch (Exception ex) { items = new List<StartupItem>(); env.Log.Warn("startup list: " + ex.Message); }

            c.Children.Add(GroupHeader("Startup apps", out var count, 34));
            count.Text = items.Count(i => i.Enabled) + " of " + items.Count + " on";
            if (items.Count == 0)
                c.Children.Add(Kit.Mono("No startup apps found.", 12, "Ink3").Margin(0, 16, 0, 0));

            foreach (var it in items)
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var sw = new ToggleButton { IsChecked = it.Enabled, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0) };
                sw.SetResourceReference(StyleProperty, "Switch");
                sw.IsEnabled = CanChange && (!it.NeedsAdmin || env.IsAdmin || env.Sandbox) && !(env.ForeignUser && it.ApprovedHive == Hive.HKCU);
                sw.ToolTip = it.Enabled ? "On: starts when you sign in" : "Off: doesn't start automatically";
                System.Windows.Automation.AutomationProperties.SetName(sw, it.DisplayName);
                var item = it;
                sw.Click += (s, e) =>
                {
                    bool on = sw.IsChecked == true;
                    try
                    {
                        Startup.SetEnabled(env.Reg, env.Log, item, on);
                        sw.ToolTip = on ? "On: starts when you sign in" : "Off: doesn't start automatically";
                        count.Text = items.Count(i => i.Enabled) + " of " + items.Count + " on";
                        Toast((on ? "On: " : "Off: ") + item.DisplayName);
                    }
                    catch (Exception ex)
                    {
                        sw.IsChecked = !on;
                        Toast("Couldn't change " + item.DisplayName + ": " + ex.Message, "Bad");
                    }
                };
                g.Children.Add(sw);

                var body = new StackPanel();
                body.Children.Add(Kit.Text(it.DisplayName, 15, "Ink", "FontDisplay"));
                body.Children.Add(Kit.Text((it.Publisher ?? "Unknown publisher") + "  ·  " + it.Where, 12.5, "Ink2").Margin(0, 3, 0, 0));
                var cmd = Kit.Mono(it.Command, 11, "Ink3");
                cmd.Margin = new Thickness(0, 6, 24, 0);
                body.Children.Add(cmd);
                Grid.SetColumn(body, 1);
                g.Children.Add(body);

                var row = new Border { Child = g, Padding = new Thickness(0, 16, 0, 16), BorderThickness = new Thickness(0, 0, 0, 1) };
                row.SetResourceReference(Border.BorderBrushProperty, "Line");
                c.Children.Add(row);
            }

            var tip = Kit.Note("Good candidates to switch off: launchers and updaters (game stores, Adobe, Spotify, Teams, Discord). Keep drivers' helpers such as audio or touchpad tools, and your antivirus.", "Ink3", 12.5);
            tip.Margin = new Thickness(0, 28, 0, 0);
            tip.MaxWidth = 680;
            tip.HorizontalAlignment = HorizontalAlignment.Left;
            c.Children.Add(tip);
            return PageScroll(c);
        }

        // ---- undo & restore ------------------------------------------------------------

        List<RestorePointInfo> restorePoints;
        bool restoreLoading;

        FrameworkElement BuildUndoPage()
        {
            var c = new StackPanel();
            c.Children.Add(Header("08", "Undo & restore", "Every way back",
                "Changed your mind, or something feels off? Here's how to undo Simplemize, from a single setting to everything at once."));

            // A — undo with Simplemize
            c.Children.Add(GroupHeader("A — Undo with Simplemize", out var cnt, 34));
            var entries = engine.UndoableEntries.OrderByDescending(e => e.AppliedAt).ToList();
            cnt.Text = entries.Count == 0 ? "" : entries.Count + (entries.Count == 1 ? " change" : " changes");
            c.Children.Add(Kit.Text("Simplemize saved the original value of every setting below. Undo puts back exactly what was there before, no guessing.", 13.5, "Ink2").Margin(0, 14, 0, 6));
            if (entries.Count == 0)
                c.Children.Add(Kit.Mono("Nothing to undo yet. Changes you apply show up here.", 12, "Ink3").Margin(0, 10, 0, 6));
            foreach (var e in entries)
            {
                var t = engine.Find(e.TweakId);
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = new StackPanel();
                left.Children.Add(Kit.Text(t?.Title ?? e.Title, 15, "Ink", "FontDisplay"));
                left.Children.Add(Kit.Mono("applied " + e.AppliedAt + "  ·  " + e.Changes.Count + (e.Changes.Count == 1 ? " value saved" : " values saved"), 11, "Ink3").Margin(0, 4, 0, 0));
                g.Children.Add(left);
                var id = e.TweakId;
                var b = Kit.Button("Undo", "Secondary", () => { if (t != null) UndoOne(t); else UndoIds(new[] { id }); });
                b.Padding = new Thickness(16, 6, 16, 7);
                b.IsEnabled = CanChange && !busy;
                b.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(b, 1);
                g.Children.Add(b);
                var row = new Border { Child = g, Padding = new Thickness(0, 14, 0, 14), BorderThickness = new Thickness(0, 0, 0, 1) };
                row.SetResourceReference(Border.BorderBrushProperty, "Line");
                c.Children.Add(row);
            }
            if (entries.Count > 1)
            {
                var all = Kit.Button("Undo everything", "Secondary", () => UndoIds(entries.Select(x => x.TweakId).ToList()));
                all.HorizontalAlignment = HorizontalAlignment.Left;
                all.Margin = new Thickness(0, 18, 0, 0);
                all.IsEnabled = CanChange && !busy;
                c.Children.Add(all);
            }

            // removed apps
            if (engine.Backup.Removed.Count > 0)
            {
                c.Children.Add(GroupHeader("Removed apps", out var rc, 46));
                rc.Text = engine.Backup.Removed.Count.ToString();
                c.Children.Add(Kit.Text("Removed apps can't be put back by Simplemize or by System Restore. They're free to reinstall from the Microsoft Store.", 13.5, "Ink2").Margin(0, 14, 0, 6));
                foreach (var r in engine.Backup.Removed.AsEnumerable().Reverse())
                {
                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    g.Children.Add(Kit.Col(Kit.Text(r.Title, 15, "Ink", "FontDisplay"), Kit.Mono("removed " + r.At, 11, "Ink3").Margin(0, 4, 0, 0)));
                    var q = r.StoreQuery ?? r.Title;
                    var link = Kit.Link("find in Microsoft Store →", () => Open("ms-windows-store://search/?query=" + Uri.EscapeDataString(q)));
                    link.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(link, 1);
                    g.Children.Add(link);
                    var row = new Border { Child = g, Padding = new Thickness(0, 14, 0, 14), BorderThickness = new Thickness(0, 0, 0, 1) };
                    row.SetResourceReference(Border.BorderBrushProperty, "Line");
                    c.Children.Add(row);
                }
            }

            // B — system restore
            c.Children.Add(GroupHeader("B — Rewind with System Restore", out _, 46));
            c.Children.Add(Kit.Text("A restore point rewinds Windows' settings, drivers and installed programs to the moment it was made. Your personal files (documents, photos, downloads) are not touched. Simplemize makes one automatically before its first change in every session.", 13.5, "Ink2").Margin(0, 14, 0, 16));

            var rpBox = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            FillRestorePoints(rpBox);
            c.Children.Add(rpBox);
            if (engine.Backup.SystemRestoreTurnedOn != null)
            {
                var srNote = Kit.Note("System Restore was off, and Simplemize turned it on (" + engine.Backup.SystemRestoreTurnedOn + ") so it could make a restore point. To switch it off again: Start › type \u201Ccreate a restore point\u201D › Configure. Switching it off deletes the restore points on that drive.", "Ink2", 12.5);
                srNote.Margin = new Thickness(0, 0, 0, 18);
                srNote.MaxWidth = 680;
                srNote.HorizontalAlignment = HorizontalAlignment.Left;
                c.Children.Add(srNote);
            }

            var steps = new StackPanel();
            steps.Children.Add(Kit.Step("1", StepText("Press ", "Win + R", ", type ", "rstrui", " and press Enter. Or use the button below.")));
            steps.Children.Add(Kit.Step("2", StepText("If Windows suggests a restore point, select ", "Choose a different restore point", ". Click Next and pick ", Engine.RestorePointName, " with the date you want to go back to.")));
            steps.Children.Add(Kit.Step("3", StepText("Click Next, then ", "Finish", ". The PC restarts and rewinds. This takes 5 to 20 minutes; don't switch it off.")));
            c.Children.Add(steps);
            var open = Kit.Button("Open System Restore", "Secondary", () => Open(Path.Combine(Environment.SystemDirectory, "rstrui.exe")));
            var make = Kit.Button("Create a restore point now", "Secondary", null,
                "Makes a restore point right away, without changing anything else. Useful to check that restore points work on this PC.");
            make.Click += async (s, e) =>
            {
                if (busy) return;
                busy = true;
                make.IsEnabled = false;
                make.Content = "Creating… (up to a minute)";
                UpdateBar();
                var res = await Task.Run(() => engine.MakeRestorePoint(null));
                busy = false;
                UpdateBar();
                restorePoints = null;   // reload the list
                ShowPage("undo");
                if (!res.Ok) Toast("Couldn't create a restore point. " + res.Message, "Bad");
                else if (res.Simulated) Toast("Sandbox: a restore point was simulated. Nothing was created in Windows.");
                else Toast("Restore point created: “" + res.Point.Description + "” at " + res.Point.Created.ToString("HH:mm", CultureInfo.InvariantCulture) + "." + (res.Notes.Count > 0 ? " " + string.Join(" ", res.Notes) : ""));
            };
            make.IsEnabled = CanChange && !busy;
            make.Margin = new Thickness(10, 0, 0, 0);
            var restoreButtons = Kit.Row(open, make);
            restoreButtons.Margin = new Thickness(34, 6, 0, 0);
            c.Children.Add(restoreButtons);

            // C — when windows won't start
            c.Children.Add(GroupHeader("C — If Windows won't start", out _, 46));
            var s2 = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            s2.Children.Add(Kit.Step("1", StepText("On the sign-in screen, hold ", "Shift", " and click Restart. If you can't get that far, switch the PC off while Windows is loading, three times in a row: Windows then opens recovery by itself.")));
            s2.Children.Add(Kit.Step("2", StepText("Choose ", "Troubleshoot › Advanced options › System Restore", ".")));
            s2.Children.Add(Kit.Step("3", StepText("Pick ", Engine.RestorePointName, " and follow the same steps as above.")));
            c.Children.Add(s2);

            // files
            c.Children.Add(GroupHeader("Where Simplemize keeps its records", out _, 46));
            var files = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            files.Children.Add(FileLine("Backup of original values", env.Demo ? @"C:\ProgramData\Simplemize\backup.json" : engine.BackupPath));
            files.Children.Add(FileLine("Log of every change", env.Demo ? @"C:\ProgramData\Simplemize\simplemize.log" : env.Log.FilePath));
            c.Children.Add(files);
            var folder = Kit.Link("open folder →", () => Open(env.DataDir));
            folder.HorizontalAlignment = HorizontalAlignment.Left;
            folder.Margin = new Thickness(0, 8, 0, 0);
            c.Children.Add(folder);
            return PageScroll(c);
        }

        void FillRestorePoints(StackPanel box)
        {
            box.Children.Clear();
            if (!env.IsAdmin && !env.Sandbox)
            {
                box.Children.Add(Kit.Mono("Run as administrator to see the restore points on this PC.", 11.5, "Ink3"));
                return;
            }
            if (restorePoints == null)
            {
                box.Children.Add(Kit.Mono("Looking for restore points…", 11.5, "Ink3"));
                if (renderMode) { restorePoints = env.Sys.ListRestorePoints(); FillRestorePoints(box); return; }
                if (!restoreLoading)
                {
                    restoreLoading = true;
                    Task.Run(() => env.Sys.ListRestorePoints()).ContinueWith(t => Dispatcher.Invoke(() =>
                    {
                        restorePoints = t.Result;
                        restoreLoading = false;
                        FillRestorePoints(box);
                    }));
                }
                return;
            }
            var ours = restorePoints.Where(r => r.Description != null && r.Description.StartsWith("Simplemize", StringComparison.OrdinalIgnoreCase)).ToList();
            var other = restorePoints.Except(ours).Take(3).ToList();
            if (restorePoints.Count == 0)
            {
                box.Children.Add(Kit.Note("There are no restore points on this PC yet. Simplemize creates one before your first change (and turns System Restore on for your system drive if it's off).", "Ink3", 12.5));
                return;
            }
            box.Children.Add(Kit.Label(ours.Count > 0 ? "Restore points on this PC" : "Restore points on this PC (none from Simplemize yet)", "Ink3", 10).Margin(0, 0, 0, 6));
            if (env.Sandbox && !env.Demo && ours.Any(r => r.Description.EndsWith("(sandbox)")))
                box.Children.Add(Kit.Note("Sandbox: points marked (sandbox) are simulated. They don't exist in Windows and won't appear in System Restore.", "Accent", 12).Margin(0, 0, 0, 8));
            foreach (var r in ours.Concat(other))
            {
                bool mine = ours.Contains(r);
                var line = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 2) };
                line.SetResourceReference(TextBlock.FontFamilyProperty, "FontMono");
                line.Inlines.Add(Kit.Run(r.Created.ToString("yyyy-MM-dd  HH:mm", CultureInfo.InvariantCulture) + "   ", "Ink3"));
                line.Inlines.Add(Kit.Run(r.Description, mine ? "Ink" : "Ink2", mine ? FontWeights.SemiBold : FontWeights.Normal));
                box.Children.Add(line);
            }
        }

        static TextBlock StepText(params string[] parts)
        {
            // alternate plain / emphasised parts
            var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 20 };
            t.SetResourceReference(TextBlock.FontFamilyProperty, "FontBody");
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            for (int i = 0; i < parts.Length; i++)
                t.Inlines.Add(i % 2 == 1 ? Kit.Run(parts[i], "Ink", FontWeights.SemiBold) : new Run(parts[i]));
            return t;
        }

        static FrameworkElement FileLine(string label, string path)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            sp.Children.Add(Kit.Text(label, 13, "Ink2"));
            sp.Children.Add(Kit.Mono(path, 11.5, "Ink").Margin(0, 2, 0, 0));
            return sp;
        }

        void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Toast("Couldn't open it: " + ex.Message, "Bad"); }
        }

        async void UndoIds(IList<string> ids)
        {
            if (busy || !CanChange) return;
            busy = true;
            UpdateBar();
            var res = await Task.Run(() => engine.Undo(ids));
            busy = false;
            await RefreshStatesAsync(false);
            ShowPage("undo");
            if (res.Failed > 0) Toast(res.Undone + " undone, " + res.Failed + " couldn't be fully undone: " + res.Errors.FirstOrDefault(), "Bad");
            else Toast(res.Undone + (res.Undone == 1 ? " change undone" : " changes undone") + AfterHint(res.After));
        }
    }
}
