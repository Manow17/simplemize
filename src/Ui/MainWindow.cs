using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Simplemize.Ui
{
    public sealed partial class MainWindow : Window
    {
        public const string Version = "1.3.0";

        readonly Engine engine;
        readonly Env env;
        readonly IList<Tweak> tweaks;
        readonly bool renderMode;

        readonly HashSet<string> selected = new HashSet<string>();
        readonly Dictionary<string, TweakRow> rows = new Dictionary<string, TweakRow>();
        readonly Dictionary<string, FrameworkElement> sectionPages = new Dictionary<string, FrameworkElement>();
        readonly Dictionary<string, SectionView> sectionViews = new Dictionary<string, SectionView>();
        readonly Dictionary<string, RadioButton> nav = new Dictionary<string, RadioButton>();
        readonly Dictionary<string, TextBlock> navNum = new Dictionary<string, TextBlock>();
        readonly Dictionary<string, TextBlock> navCount = new Dictionary<string, TextBlock>();

        Grid root, overlay, toastHost;
        ContentControl host;
        TextBlock barCount, barMain, barSub;
        Button applyBtn, clearBtn;
        string page = "overview";
        string filter = "all";
        bool busy, suppressNav, statesLoaded;
        Snapshot lastSnap;

        sealed class PageInfo
        {
            public string Key, Num, Label, Title, Lede;
            public Section? Section;
        }

        static readonly PageInfo[] PageList =
        {
            new PageInfo { Key = "overview", Num = "01", Label = "Overview" },
            new PageInfo { Key = "performance", Num = "02", Label = "Performance", Section = Simplemize.Section.Performance,
                Title = "Performance",
                Lede = "Lower input delay, steadier frame times and less work for the processor. Most of these are small switches that add up. The one big lever sits at the bottom, under Security trade-offs." },
            new PageInfo { Key = "hardware", Num = "03", Label = "Hardware", Section = Simplemize.Section.Hardware,
                Title = "Hardware",
                Lede = "Settings outside Windows itself: how your processor boosts, your NVIDIA driver's global settings, your display's refresh rate and device power saving. Power changes apply only when the PC is plugged in, so battery life on laptops stays the same." },
            new PageInfo { Key = "debloat", Num = "04", Label = "Debloat", Section = Simplemize.Section.Debloat,
                Title = "Debloat",
                Lede = "Switch off tracking, ads and features you didn't ask for, and remove preinstalled apps. Each app is listed on its own, so nothing goes that you didn't tick." },
            new PageInfo { Key = "processes", Num = "05", Label = "Processes", Section = Simplemize.Section.Processes,
                Title = "Processes",
                Lede = "Fewer things running in the background. Every service change is recorded, so undo puts back the exact startup type each service had." },
            new PageInfo { Key = "cleanup", Num = "06", Label = "Cleanup", Section = Simplemize.Section.Cleanup,
                Title = "Cleanup",
                Lede = "One-time cleanups that free disk space. Sizes are measured on this PC. Simplemize leaves your shader cache and Prefetch alone on purpose: clearing them makes games stutter and apps load slower until Windows rebuilds them." },
            new PageInfo { Key = "extras", Num = "07", Label = "Extras", Section = Simplemize.Section.Extras,
                Title = "Extras",
                Lede = "Small quality-of-life changes for File Explorer, the taskbar, Windows Update and power." },
            new PageInfo { Key = "startup", Num = "08", Label = "Startup apps" },
            new PageInfo { Key = "undo", Num = "09", Label = "Undo & restore" },
        };

        sealed class GroupView
        {
            public string Name;
            public StackPanel Panel, RowsHost;
            public TextBlock Count;
            public List<TweakRow> Rows = new List<TweakRow>();
            public FrameworkElement Placeholder;
        }

        sealed class SectionView
        {
            public Section Section;
            public List<GroupView> Groups = new List<GroupView>();
            public Dictionary<string, RadioButton> Filters = new Dictionary<string, RadioButton>();
        }

        bool CanChange => (env.IsAdmin || env.Sandbox) && env.BlockedReason == null;

        public MainWindow(Env env, Engine engine, IList<Tweak> tweaks, bool renderMode = false)
        {
            this.env = env;
            this.engine = engine;
            this.tweaks = tweaks;
            this.renderMode = renderMode;

            Title = env.Sandbox ? "Simplemize (sandbox)" : "Simplemize";
            Width = 1200;
            Height = 800;
            MinWidth = 980;
            MinHeight = 640;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            this.SetResourceReference(BackgroundProperty, "Bg");
            this.SetResourceReference(FontFamilyProperty, "FontBody");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 38,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });

            foreach (var t in tweaks)
                rows[t.Id] = new TweakRow(t, OnToggle, r => UndoOne(r.T));

            root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            root.ColumnDefinitions.Add(new ColumnDefinition());
            if (!env.Info.IsWin11)
            {
                // Windows 10 doesn't draw a border around borderless windows.
                var frame = new Border { Child = root, BorderThickness = new Thickness(1) };
                frame.SetResourceReference(Border.BorderBrushProperty, "Line2");
                Content = frame;
            }
            else Content = root;

            root.Children.Add(BuildRail());
            var main = BuildMain();
            Grid.SetColumn(main, 1);
            root.Children.Add(main);

            overlay = new Grid { Visibility = Visibility.Collapsed };
            var scrim = new Border();
            scrim.SetResourceReference(Border.BackgroundProperty, "Scrim");
            overlay.Children.Add(scrim);
            Grid.SetColumnSpan(overlay, 2);
            root.Children.Add(overlay);

            StateChanged += (s, e) => FixMaximizedMargin();
            SourceInitialized += (s, e) => { RoundCorners(); FixMaximizedMargin(); };
            Theme.Changed += () => { RoundCorners(); };
            PreviewKeyDown += OnKey;
            Loaded += (s, e) => { if (!renderMode) StartBackgroundLoads(); };
            // Display changes are confirmed on screen and go back by themselves if nobody answers.
            if (!renderMode) env.ConfirmDisplay = (msg, secs) => Dispatcher.Invoke(() => AskKeepDisplay(msg, secs)).Result;

            ShowPage("overview");
            UpdateBar();
        }

        // ---- window chrome -------------------------------------------------------------

        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

        void FixMaximizedMargin()
        {
            if (WindowState == WindowState.Maximized)
            {
                double scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
                double px = GetSystemMetrics(32) + GetSystemMetrics(92);   // SM_CXSIZEFRAME + SM_CXPADDEDBORDER
                root.Margin = new Thickness(px / scale);
            }
            else root.Margin = new Thickness(0);
        }

        void RoundCorners()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero || !env.Info.IsWin11) return;
                int pref = Native.DWMWCP_ROUND;
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
                var c = Theme.C("Line2");
                int colorref = c.R | (c.G << 8) | (c.B << 16);
                Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref colorref, sizeof(int));
            }
            catch { }
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && overlay.Visibility == Visibility.Visible && !busy)
            {
                HideOverlay();
                e.Handled = true;
            }
        }

        // ---- rail ----------------------------------------------------------------------

        FrameworkElement BuildRail()
        {
            var rail = new Border { BorderThickness = new Thickness(0, 0, 1, 0) };
            rail.SetResourceReference(Border.BackgroundProperty, "Rail");
            rail.SetResourceReference(Border.BorderBrushProperty, "Line");

            var dock = new DockPanel { LastChildFill = true };
            rail.Child = dock;

            // wordmark
            var head = new StackPanel { Margin = new Thickness(24, 24, 18, 28) };
            var mark = Kit.Logo(26);
            mark.VerticalAlignment = VerticalAlignment.Center;
            var word = Kit.Display("simplemize", 22);
            word.VerticalAlignment = VerticalAlignment.Center;
            word.Margin = new Thickness(11, -3, 0, 0);
            head.Children.Add(Kit.Row(mark, word));
            head.Children.Add(Kit.Mono("v" + Version + (env.Sandbox && !env.Demo ? "  ·  sandbox" : "  ·  open source"), 10.5, "Ink3", false).Margin(37, 6, 0, 0));
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);

            var sys = BuildSystemBlock();
            DockPanel.SetDock(sys, Dock.Bottom);
            dock.Children.Add(sys);

            var list = new StackPanel();
            foreach (var p in PageList)
            {
                if (p.Key == "startup")
                {
                    list.Children.Add(Kit.Rule(1, "Line", new Thickness(24, 14, 24, 14)));
                }
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var num = Kit.Mono(p.Num, 11, "Ink3", false);
                num.VerticalAlignment = VerticalAlignment.Center;
                num.Margin = new Thickness(0, 1, 0, 0);
                var label = new TextBlock { Text = p.Label, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
                label.SetResourceReference(TextBlock.FontFamilyProperty, "FontDisplay");
                Grid.SetColumn(label, 1);
                var count = Kit.Mono("", 11, "Accent", false);
                count.FontWeight = FontWeights.Bold;
                count.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(count, 2);
                g.Children.Add(num);
                g.Children.Add(label);
                g.Children.Add(count);

                var rb = new RadioButton { Content = g, GroupName = "nav" };
                rb.SetResourceReference(StyleProperty, "Nav");
                var key = p.Key;
                rb.Checked += (s, e) =>
                {
                    foreach (var kv in navNum) kv.Value.SetResourceReference(TextBlock.ForegroundProperty, kv.Key == key ? "Accent" : "Ink3");
                    if (!suppressNav) ShowPage(key);
                };
                System.Windows.Automation.AutomationProperties.SetName(rb, p.Label);
                nav[key] = rb;
                navNum[key] = num;
                navCount[key] = count;
                list.Children.Add(rb);
            }
            dock.Children.Add(list);
            return rail;
        }

        FrameworkElement BuildSystemBlock()
        {
            var sp = new StackPanel { Margin = new Thickness(24, 12, 20, 22) };
            sp.Children.Add(Kit.Label("This PC").Margin(0, 0, 0, 8));
            var i = env.Info;
            void Line(string s, string brush = "Ink2", double size = 12)
            {
                var t = Kit.Text(s, size, brush, "FontBody", null, false);
                t.Margin = new Thickness(0, 0, 0, 3);
                t.ToolTip = s;
                sp.Children.Add(t);
            }
            Line(i.OsName + (string.IsNullOrEmpty(i.OsRelease) ? "" : " " + i.OsRelease));
            Line(i.Cpu);
            Line(i.Gpu);
            Line(Math.Round(i.RamBytes / 1073741824.0) + " GB memory · " + (i.IsLaptop ? "laptop" : "desktop"));
            bool sandboxed = env.Sandbox && !env.Demo;
            var mode = sandboxed ? "sandbox: nothing is changed" : env.IsAdmin || env.Demo ? "administrator" : "read-only (not administrator)";
            var modeText = Kit.Mono("● " + mode, 10.5, sandboxed ? "Accent" : env.IsAdmin || env.Demo ? "Good" : "Warn");
            modeText.Margin = new Thickness(0, 6, 0, 16);
            sp.Children.Add(modeText);

            var seg = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var name in new[] { "Light", "Dark" })
            {
                var rb = new RadioButton { Content = name, GroupName = "theme", IsChecked = (name == "Dark") == Theme.Dark };
                rb.SetResourceReference(StyleProperty, "Seg");
                bool dark = name == "Dark";
                rb.Checked += (s, e) => { if (Theme.Dark != dark) { Theme.Apply(dark); Theme.Remember(dark); } };
                seg.Children.Add(rb);
            }
            sp.Children.Add(seg);
            return sp;
        }

        // ---- main column ---------------------------------------------------------------

        FrameworkElement BuildMain()
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // caption buttons
            var caps = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            var min = Kit.Button("\uE921", "Caption", () => WindowState = WindowState.Minimized, "Minimize");
            var max = Kit.Button("\uE922", "Caption", () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized, "Maximize");
            var close = Kit.Button("\uE8BB", "CaptionClose", Close, "Close");
            StateChanged += (s, e) => max.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            foreach (var b in new[] { min, max, close })
            {
                WindowChrome.SetIsHitTestVisibleInChrome(b, true);
                caps.Children.Add(b);
            }
            g.Children.Add(caps);

            // banner (read-only / sandbox)
            var banner = BuildBanner();
            if (banner != null)
            {
                Grid.SetRow(banner, 1);
                g.Children.Add(banner);
            }

            host = new ContentControl { Focusable = false };
            Grid.SetRow(host, 2);
            g.Children.Add(host);

            toastHost = new Grid { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 28, 0) };
            Grid.SetRow(toastHost, 2);
            g.Children.Add(toastHost);

            var bar = BuildActionBar();
            Grid.SetRow(bar, 3);
            g.Children.Add(bar);
            return g;
        }

        FrameworkElement BuildBanner()
        {
            if (env.Demo || (!env.Sandbox && env.IsAdmin && env.BlockedReason == null && !env.ForeignUser)) return null;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string msg = env.Sandbox
                ? "Sandbox mode: nothing on this PC is changed. Every change is simulated, so you can try everything safely."
                : env.BlockedReason != null
                ? "Read-only: " + env.BlockedReason
                : !env.IsAdmin
                ? "Read-only. Simplemize isn't running as administrator, so it can show you everything but can't change anything."
                : "Simplemize is running as a different Windows account than the one signed in. Per-user changes are switched off so they don't land in the wrong account; system-wide changes still work.";
            var t = Kit.Text(msg, 13, "Ink");
            t.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(t);
            if (!env.Sandbox && !env.IsAdmin)
            {
                var b = Kit.Link("restart as administrator", () => Program.RelaunchElevated(this));
                b.Foreground = null;
                b.SetResourceReference(ForegroundProperty, "Ink");
                b.FontWeight = FontWeights.SemiBold;
                b.Margin = new Thickness(16, 0, 0, 0);
                Grid.SetColumn(b, 1);
                g.Children.Add(b);
            }
            var border = new Border { Child = g, Padding = new Thickness(48, 10, 32, 11), Margin = new Thickness(0, 0, 0, 4) };
            border.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            return border;
        }

        FrameworkElement BuildActionBar()
        {
            var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(48, 14, 28, 16) };
            border.SetResourceReference(Border.BorderBrushProperty, "Line");
            border.SetResourceReference(Border.BackgroundProperty, "Bg");
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            border.Child = g;

            barCount = Kit.Display("0", 32);
            barCount.VerticalAlignment = VerticalAlignment.Center;
            barCount.MinWidth = 28;
            barMain = Kit.Text("", 14, "Ink", "FontDisplay");
            barSub = Kit.Mono("", 11, "Ink2");
            var txt = Kit.Col(barMain, barSub);
            txt.VerticalAlignment = VerticalAlignment.Center;
            txt.Margin = new Thickness(14, 0, 0, 0);
            g.Children.Add(Kit.Row(barCount, txt));

            clearBtn = Kit.Button("Clear", "Secondary", ClearSelection);
            clearBtn.Margin = new Thickness(0, 0, 10, 0);
            applyBtn = Kit.Button("Review & apply", "Primary", OpenReview);
            applyBtn.MinWidth = 170;
            var btns = Kit.Row(clearBtn, applyBtn);
            btns.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(btns, 1);
            g.Children.Add(btns);
            return border;
        }

        // ---- navigation ----------------------------------------------------------------

        public void ShowPage(string key)
        {
            page = key;
            suppressNav = true;
            if (nav.TryGetValue(key, out var rb)) rb.IsChecked = true;
            suppressNav = false;

            var info = PageList.First(p => p.Key == key);
            FrameworkElement el;
            if (info.Section != null)
            {
                if (!sectionPages.TryGetValue(key, out el))
                {
                    el = BuildSectionPage(info);
                    sectionPages[key] = el;
                }
                ApplyFilter(sectionViews[key]);
            }
            else if (key == "overview") el = BuildOverview();
            else if (key == "startup") el = BuildStartupPage();
            else el = BuildUndoPage();

            if (el.Parent is ContentControl old && old != host) old.Content = null;
            host.Content = el;
        }

        public string CurrentPage => page;

        // ---- selection -----------------------------------------------------------------

        void OnToggle(TweakRow row, bool on)
        {
            if (on) selected.Add(row.T.Id); else selected.Remove(row.T.Id);
            UpdateBar();
        }

        void SetSelected(Tweak t, bool on)
        {
            var r = rows[t.Id];
            if (on && !r.Selectable) return;
            r.Selected = on;
            if (on) selected.Add(t.Id); else selected.Remove(t.Id);
        }

        void ClearSelection()
        {
            foreach (var id in selected.ToList()) rows[id].Selected = false;
            selected.Clear();
            UpdateBar();
        }

        bool PresetPick(Tweak t, Preset p)
        {
            if (!t.Presets.HasFlag(p)) return false;
            var r = rows[t.Id];
            if (r.Unavailable != null || r.Warning != null) return false;   // presets only take what suits this PC
            if (t.OneShot) return true;
            return r.State == TState.Off || r.State == TState.Partial;
        }

        public int CountPreset(Preset p) => tweaks.Count(t => PresetPick(t, p));

        public int SelectPreset(Preset p)
        {
            ClearSelection();
            foreach (var t in tweaks)
                if (PresetPick(t, p)) SetSelected(t, true);
            UpdateBar();
            return selected.Count;
        }

        List<Tweak> SelectedTweaks()
        {
            // Apply in catalog order, heavy cleanups (DISM) last.
            return tweaks.Where(t => selected.Contains(t.Id)).OrderBy(t => t.Slow ? 1 : 0).ToList();
        }

        void UpdateBar()
        {
            if (barCount == null) return;
            var sel = SelectedTweaks();
            int heavy = sel.Count(t => t.Level == Level.Heavy);
            barCount.Text = sel.Count.ToString();
            if (sel.Count == 0)
            {
                barMain.Text = "Nothing selected yet";
                barSub.Text = "Tick changes on any page, or start from a preset on Overview.";
            }
            else
            {
                barMain.Text = sel.Count == 1 ? "change selected" : "changes selected";
                var parts = new List<string> { heavy == 0 ? "all minor" : heavy + " heavy, " + (sel.Count - heavy) + " minor" };
                var after = sel.Aggregate(After.None, (a, t) => a | t.After);
                if (after.HasFlag(After.Restart)) parts.Add("restart needed after");
                else if (after.HasFlag(After.SignOut)) parts.Add("sign out after");
                if (sel.Any(t => !t.Revertible && !t.OneShot)) parts.Add("includes removals");
                barSub.Text = string.Join("  ·  ", parts);
            }
            applyBtn.IsEnabled = sel.Count > 0 && CanChange && !busy;
            applyBtn.ToolTip = CanChange ? null : "Run Simplemize as administrator to apply changes.";
            clearBtn.IsEnabled = sel.Count > 0 && !busy;

            foreach (var p in PageList.Where(x => x.Section != null))
            {
                int n = sel.Count(t => t.Section == p.Section);
                navCount[p.Key].Text = n > 0 ? n.ToString() : "";
            }
        }

        // ---- state ---------------------------------------------------------------------

        sealed class RowState
        {
            public TState State;
            public string Unavailable, Warning;
            public long Size = -2;
        }

        Dictionary<string, RowState> ComputeStates(bool withSizes)
        {
            var d = new Dictionary<string, RowState>();
            foreach (var t in tweaks)
            {
                var s = new RowState();
                s.Unavailable = t.Reason(env);
                try { s.Warning = t.Warning?.Invoke(env); } catch { }
                s.State = s.Unavailable != null ? TState.NotApplicable : t.Evaluate(env);
                if (withSizes && t.Measure != null)
                {
                    try { s.Size = t.Measure(env); } catch { s.Size = -1; }
                }
                d[t.Id] = s;
            }
            return d;
        }

        void ApplyStates(Dictionary<string, RowState> d)
        {
            foreach (var kv in d)
            {
                var r = rows[kv.Key];
                r.State = kv.Value.State;
                r.Unavailable = kv.Value.Unavailable;
                r.Warning = kv.Value.Warning;
                if (kv.Value.Size != -2) r.Size = kv.Value.Size;
                r.HasBackup = engine.HasUndoFor(kv.Key);
                r.Update();
                if (!r.Selected) selected.Remove(kv.Key);
            }
            statesLoaded = true;
            foreach (var sv in sectionViews.Values) { RebuildApps(sv); ApplyFilter(sv); }
            UpdateBar();
        }

        /// <summary>Synchronous load used by --render.</summary>
        public void LoadAllNow()
        {
            if (env.Apps == null) env.Apps = env.Sys.ListApps();
            if (!env.Demo) env.Info.SystemOnHdd = SysInfo.DetectSystemOnHdd();
            ApplyStates(ComputeStates(true));
            lastSnap = env.Demo
                ? new Snapshot { Processes = 168, Services = 94, RamUsed = 10_629_000_000L, RamTotal = 34_359_738_368L }
                : Snapshot.Take();
            if (page == "overview") ShowPage("overview");
        }

        async void StartBackgroundLoads()
        {
            await RefreshStatesAsync(true);
            var appsTask = Task.Run(() => env.Sys.ListApps());
            var hddTask = Task.Run(() => SysInfo.DetectSystemOnHdd());
            env.Info.SystemOnHdd = await hddTask;
            env.Apps = await appsTask;
            await RefreshStatesAsync(false);
        }

        public async Task RefreshStatesAsync(bool withSizes)
        {
            var d = await Task.Run(() => ComputeStates(withSizes));
            ApplyStates(d);
            if (page == "overview" || page == "undo") ShowPage(page);
        }

        // ---- toast ---------------------------------------------------------------------

        public void Toast(string text, string brush = "Ink")
        {
            toastHost.Children.Clear();
            var t = Kit.Text(text, 13, "Bg");
            t.MaxWidth = 420;
            var b = new Border { Child = t, Padding = new Thickness(14, 9, 14, 10), CornerRadius = new CornerRadius(2) };
            b.SetResourceReference(Border.BackgroundProperty, brush);
            toastHost.Children.Add(b);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (s, e) => { timer.Stop(); toastHost.Children.Remove(b); };
            timer.Start();
        }

        // ---- undo from a row -----------------------------------------------------------

        async void UndoOne(Tweak t)
        {
            if (busy || !CanChange) return;
            busy = true;
            UpdateBar();
            var res = await Task.Run(() => engine.Undo(new[] { t.Id }));
            busy = false;
            await RefreshStatesAsync(false);
            if (res.Failed > 0) Toast("Couldn't fully undo “" + t.Title + "”: " + res.Errors.FirstOrDefault(), "Bad");
            else Toast("Undone: " + t.Title + AfterHint(res.After));
        }

        static string AfterHint(After a)
        {
            if (a.HasFlag(After.Restart)) return ". Restart your PC to finish.";
            if (a.HasFlag(After.SignOut)) return ". Sign out and back in to finish.";
            if (a.HasFlag(After.Explorer)) return ". Restart Explorer (or sign out) to see it.";
            return ".";
        }
    }
}
