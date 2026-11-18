using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Simplemize.Ui;

namespace Simplemize
{
    /// <summary>--render: opens the real window off-screen (in the sandbox) and saves every page as a PNG.</summary>
    public static class Renderer
    {
        public static int Run(Application app, Env env, Engine engine, IList<Tweak> catalog, string dir, double scale, string only)
        {
            Directory.CreateDirectory(dir);
            int code = 0;
            app.Startup += (s, e) =>
            {
                try
                {
                    foreach (var dark in only == "dark" ? new[] { true } : only == "light" ? new[] { false } : new[] { false, true })
                        Shoot(env, engine, catalog, dir, scale, dark);
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(dir, "render-error.txt"), ex.ToString());
                    code = 1;
                }
                app.Shutdown(code);
            };
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.Run();
            return code;
        }

        static void Shoot(Env env, Engine engine, IList<Tweak> catalog, string dir, double scale, bool dark)
        {
            Theme.Apply(dark);
            string pfx = dark ? "dark-" : "light-";
            var w = new MainWindow(env, engine, catalog, renderMode: true)
            {
                Width = 1200,
                Height = 800,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.VirtualScreenLeft - 4000,
                Top = 0,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            w.Show();
            w.LoadAllNow();
            Pump();

            var pages = dark ? new[] { "overview", "performance", "debloat" }
                             : new[] { "overview", "performance", "debloat", "processes", "cleanup", "extras", "startup", "undo" };
            foreach (var p in pages)
            {
                w.ShowPage(p);
                Save(w, Path.Combine(dir, pfx + p + ".png"), scale);
            }

            // details sheet open
            w.ShowPage("performance");
            w.ExpandForShot("power.plan");
            Save(w, Path.Combine(dir, pfx + "details.png"), scale);
            w.ExpandForShot("power.plan");

            // gamer preset + review dialog
            w.SelectPreset(Preset.Gamer);
            w.ShowPage("performance");
            Save(w, Path.Combine(dir, pfx + "selected.png"), scale);
            w.OpenReview();
            Save(w, Path.Combine(dir, pfx + "review.png"), scale);

            if (!dark)
            {
                // run the real apply flow in the sandbox and capture the finish screen
                w.ConfirmReviewForShot();
                var sw = Stopwatch.StartNew();
                while (w.IsBusyForShot && sw.ElapsedMilliseconds < 120000) { Pump(); Thread.Sleep(40); }
                Pump();
                Thread.Sleep(300);
                Pump();
                Save(w, Path.Combine(dir, pfx + "done.png"), scale);
                w.CloseOverlayForShot();
                w.ShowPage("undo");
                Save(w, Path.Combine(dir, pfx + "undo-after.png"), scale);
                w.ShowPage("overview");
                Save(w, Path.Combine(dir, pfx + "overview-after.png"), scale);
            }
            w.Close();
        }

        static void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }

        static void Save(Window w, string file, double scale)
        {
            for (int i = 0; i < 3; i++) Pump();
            var visual = (FrameworkElement)w.Content;
            visual.UpdateLayout();
            int pw = (int)Math.Round(visual.ActualWidth * scale), ph = (int)Math.Round(visual.ActualHeight * scale);
            var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Theme.B("Bg"), null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
                dc.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                                 null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
            }
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);
        }
    }
}
