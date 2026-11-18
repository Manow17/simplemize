using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Simplemize
{
    /// <summary>
    /// Every change Simplemize can make. Each entry says, in plain words, what it does, what you get,
    /// what you give up, and lists the exact registry values / services / tasks it touches.
    ///
    /// Minor = small, targeted, fully reversible, no feature disappears.
    /// Heavy = changes a lot, switches a Windows feature off, removes something, or lowers a protection.
    /// </summary>
    public static class Catalog
    {
        const Hive LM = Hive.HKLM, CU = Hive.HKCU;
        const Preset All3 = Preset.Light | Preset.Gamer | Preset.Stripped;
        const Preset GS = Preset.Gamer | Preset.Stripped;
        const Preset S = Preset.Stripped;

        static RegSet Dw(Hive h, string path, string name, int value, string windowsDefault = null)
            => new RegSet(h, path, name, RegistryValueKind.DWord, value, windowsDefault);

        static RegSet Sz(Hive h, string path, string name, string value, string windowsDefault = null)
            => new RegSet(h, path, name, RegistryValueKind.String, value, windowsDefault);

        static string[] A(params string[] a) => a;

        static string Win11Only(Env env) => env.Info.IsWin11 ? null : "Windows 11 only";
        static string Laptop(Env env) => env.Info.IsLaptop ? "This PC runs on a battery: expect shorter battery life while this is on." : null;

        public static List<Tweak> Build(Env env)
        {
            var list = new List<Tweak>();
            Performance(list, env);
            Hardware(list);
            Debloat(list, env);
            Processes(list);
            Cleanup(list);
            Extras(list);
            return list;
        }

        // ===================================================================================
        // PERFORMANCE
        // ===================================================================================
        static void Performance(List<Tweak> list, Env env)
        {
            const string sp = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
            const string games = sp + @"\Tasks\Games";

            list.Add(new Tweak
            {
                Id = "power.plan", Section = Section.Performance, Group = "Power & CPU", Level = Level.Minor,
                Title = "Ultimate Performance power plan",
                Summary = "Keeps the processor at full readiness so it never has to wake up from a power-saving state in the middle of a frame.",
                Gain = "More even frame times and a little less input delay. The biggest difference is on PCs that were on the Balanced plan.",
                TradeOff = "Higher power use and a bit more heat when the PC is idle.",
                UndoNote = "Fully reversible. Undo switches back to the plan you had before and deletes the copy.",
                Affects = A("input delay", "fps"),
                Presets = GS,
                Warning = Laptop,
                Actions = { new PowerPlanAction() },
            });

            list.Add(new Tweak
            {
                Id = "cpu.foreground", Section = Section.Performance, Group = "Power & CPU", Level = Level.Minor,
                Title = "Favor the app you're using",
                Summary = "Gives the window in front (your game) shorter, more frequent turns on the processor plus a priority boost, instead of treating every program the same.",
                Gain = "The active app responds faster when other programs are busy in the background.",
                TradeOff = "Background work like downloads or video exports runs slightly slower while you play.",
                Affects = A("input delay"),
                Presets = GS,
                Actions = { Dw(LM, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26, "2") },
            });

            list.Add(new Tweak
            {
                Id = "cpu.mmcss", Section = Section.Performance, Group = "Power & CPU", Level = Level.Minor,
                Title = "Put games first in the multimedia scheduler",
                Summary = "Windows has a scheduler for audio, video and games. This raises the processor and disk priority it gives to games and reserves less time for background tasks.",
                Gain = "Steadier frame pacing in games that register with the scheduler (many do, through DirectX and their audio engine).",
                TradeOff = "Nothing you'll notice in normal use. Background tasks get a smaller slice while a game runs.",
                Affects = A("fps", "input delay"),
                Presets = GS,
                Actions =
                {
                    Dw(LM, sp, "SystemResponsiveness", 10, "20"),
                    Dw(LM, games, "Priority", 6, "2"),
                    Sz(LM, games, "Scheduling Category", "High", "\"Medium\""),
                    Sz(LM, games, "SFIO Priority", "High", "\"Normal\""),
                },
            });

            list.Add(new Tweak
            {
                Id = "power.throttling", Section = Section.Performance, Group = "Power & CPU", Level = Level.Minor,
                Title = "Turn off Power Throttling",
                Summary = "Stops Windows from putting programs in the background into a low-power \"efficiency mode\".",
                Gain = "Apps you keep running behind your game (streaming and recording software, voice chat, downloads) keep their full speed.",
                TradeOff = "Uses more power. Not worth it on a laptop that runs on battery.",
                Affects = A("background apps"),
                Presets = S,
                Warning = Laptop,
                Actions = { Dw(LM, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1, "not set") },
            });

            // ---- games & graphics
            list.Add(new Tweak
            {
                Id = "game.mode", Section = Section.Performance, Group = "Games & graphics", Level = Level.Minor,
                Title = "Game Mode on",
                Summary = "Makes sure Game Mode is switched on. While you play, it stops Windows Update from installing drivers or restarting and gives the game priority.",
                Gain = "Fewer stutters caused by Windows doing maintenance in the middle of a match.",
                TradeOff = "None. It's on by default; this just makes sure nothing switched it off.",
                Affects = A("fps"),
                Presets = All3,
                Actions =
                {
                    new RegSet(CU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValueKind.DWord, 1, "1") { MissingMeans = 1 },
                    new RegSet(CU, @"Software\Microsoft\GameBar", "AllowAutoGameMode", RegistryValueKind.DWord, 1, "1") { MissingMeans = 1 },
                },
            });

            list.Add(new Tweak
            {
                Id = "game.dvr", Section = Section.Performance, Group = "Games & graphics", Level = Level.Minor,
                Title = "Turn off background game recording",
                Summary = "Switches off Game DVR, the Xbox Game Bar feature that can record your gameplay in the background.",
                Gain = "If background recording was on, you get back the graphics and disk time it used, often a few FPS. If it was off, this just removes the capture hooks from games.",
                TradeOff = "Win+Alt+R recording and \"Record what happened\" stop working. The Game Bar overlay still opens. Your graphics card's own recorder (NVIDIA ShadowPlay, AMD ReLive) is unaffected.",
                Affects = A("fps"),
                Presets = GS,
                Actions =
                {
                    Dw(CU, @"System\GameConfigStore", "GameDVR_Enabled", 0, "1"),
                    Dw(CU, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, "1"),
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", 0, "not set"),
                },
            });

            list.Add(new Tweak
            {
                Id = "gpu.hags", Section = Section.Performance, Group = "Games & graphics", Level = Level.Minor,
                Title = "Hardware-accelerated GPU scheduling",
                Summary = "Lets the graphics card manage its own work queue instead of Windows doing it on the processor.",
                Gain = "Slightly lower input delay and less load on the processor. NVIDIA's DLSS Frame Generation requires it.",
                TradeOff = "Needs a graphics card from around 2017 or newer (GeForce GTX 10-series, Radeon RX 5000, Intel Arc) and an up-to-date driver. A few older games stutter with it on; if that happens, undo it.",
                Affects = A("input delay", "fps"),
                After = After.Restart,
                Presets = GS,
                Actions = { Dw(LM, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, "1 (off) or not set") },
            });

            list.Add(new Tweak
            {
                Id = "gpu.windowed", Section = Section.Performance, Group = "Games & graphics", Level = Level.Minor,
                Title = "Optimizations for windowed games",
                Summary = "Moves older DirectX 10 and 11 games that run in a window or borderless mode to the modern \"flip\" way of showing frames.",
                Gain = "Less input delay in borderless and windowed games, plus support for Auto HDR and variable refresh rate there.",
                TradeOff = "Rarely, an old game shows a black screen or a glitchy overlay. You can turn it off for that one game in Settings › Display › Graphics.",
                Affects = A("input delay"),
                Presets = GS,
                Unavailable = Win11Only, Requires = "Windows 11",
                Actions = { new RegPair(CU, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", "SwapEffectUpgradeEnable", "1") },
            });

            // ---- input
            list.Add(new Tweak
            {
                Id = "input.mouse", Section = Section.Performance, Group = "Input", Level = Level.Minor,
                Title = "Turn off mouse acceleration",
                Summary = "Switches off \"Enhance pointer precision\", so the cursor moves exactly as far as your hand does, however fast you move it.",
                Gain = "Consistent, predictable aim. It takes effect immediately.",
                TradeOff = "The desktop cursor can feel slow at first. Raise the pointer speed or your mouse's DPI if it does.",
                Affects = A("aim"),
                Presets = GS,
                Actions =
                {
                    Sz(CU, @"Control Panel\Mouse", "MouseSpeed", "0", "\"1\""),
                    Sz(CU, @"Control Panel\Mouse", "MouseThreshold1", "0", "\"6\""),
                    Sz(CU, @"Control Panel\Mouse", "MouseThreshold2", "0", "\"10\""),
                },
            });

            list.Add(new Tweak
            {
                Id = "input.stickykeys", Section = Section.Performance, Group = "Input", Level = Level.Minor,
                Title = "No Sticky Keys pop-up",
                Summary = "Stops the Sticky Keys, Filter Keys and Toggle Keys prompts that appear when you tap Shift five times or hold a key in a game.",
                Gain = "No more being thrown back to the desktop mid-game.",
                TradeOff = "Only the keyboard shortcuts are turned off. If you use Sticky Keys or Filter Keys, they stay on, and every other accessibility setting is kept. Takes effect after you sign out.",
                Affects = A("input"),
                After = After.SignOut,
                Presets = All3,
                Actions =
                {
                    new RegBitsClear(CU, @"Control Panel\Accessibility\StickyKeys", "Flags", 0x4, 510, "Shift five times shortcut"),
                    new RegBitsClear(CU, @"Control Panel\Accessibility\Keyboard Response", "Flags", 0x4, 126, "hold Right Shift shortcut"),
                    new RegBitsClear(CU, @"Control Panel\Accessibility\ToggleKeys", "Flags", 0x4, 62, "hold Num Lock shortcut"),
                },
            });

            list.Add(new Tweak
            {
                Id = "input.keyrepeat", Section = Section.Performance, Group = "Input", Level = Level.Minor,
                Title = "Shorter key-repeat delay",
                Summary = "Shortens the pause before a held key starts repeating, the same as moving the \"Repeat delay\" slider in Keyboard settings all the way to Short.",
                Gain = "Held keys (arrows, backspace, movement in some games) respond sooner. It takes effect immediately.",
                TradeOff = "If you tend to rest on keys, you may get the odd double letter at first.",
                Affects = A("input"),
                Presets = Preset.None,
                Actions = { Sz(CU, @"Control Panel\Keyboard", "KeyboardDelay", "0", "\"1\"") },
            });

            // ---- network
            var nagle = NagleActions(env);
            var nagleTweak = new Tweak
            {
                Id = "net.nagle", Section = Section.Performance, Group = "Network", Level = Level.Minor,
                Title = "Send small packets right away (Nagle off)",
                Summary = "Stops Windows from holding back small network packets so it can bundle them together, on every connected network adapter.",
                Gain = "Lower delay in games that talk over TCP: mostly MMOs and some older online games. Games that use UDP (most modern shooters) aren't affected either way.",
                TradeOff = "Slightly more network packets. Download speed doesn't change.",
                Affects = A("network delay"),
                Presets = GS,
                Unavailable = e => nagle.Count == 0 ? "No connected network adapter found" : null,
                // Adapters can come and go between sessions: undo may restore any adapter's two values.
                AlsoOwns = c => c.Type == "reg" && c.Hive == "HKLM"
                    && c.Path != null && c.Path.StartsWith(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\", StringComparison.OrdinalIgnoreCase)
                    && c.Path.IndexOf('\\', @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\".Length) < 0
                    && (string.Equals(c.Name, "TcpAckFrequency", StringComparison.OrdinalIgnoreCase) || string.Equals(c.Name, "TCPNoDelay", StringComparison.OrdinalIgnoreCase)),
            };
            nagleTweak.Actions.AddRange(nagle);
            list.Add(nagleTweak);

            list.Add(new Tweak
            {
                Id = "net.throttling", Section = Section.Performance, Group = "Network", Level = Level.Minor,
                Title = "No network throttling during media playback",
                Summary = "Removes the cap Windows puts on network packet processing while music or video is playing.",
                Gain = "Helps when you play with music, a video or a stream running in the background.",
                TradeOff = "A little more processor time goes to network traffic.",
                Affects = A("network delay"),
                Presets = GS,
                Actions = { Dw(LM, sp, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), "10") },
            });

            list.Add(new Tweak
            {
                Id = "net.p2p", Section = Section.Performance, Group = "Network", Level = Level.Minor,
                Title = "Stop sharing Windows updates with other PCs",
                Summary = "Turns off Delivery Optimization's peer-to-peer mode, so your PC stops uploading update files to other computers.",
                Gain = "Frees upload bandwidth that can otherwise cause lag spikes.",
                TradeOff = "Updates always download straight from Microsoft.",
                Affects = A("network"),
                Presets = All3,
                Actions = { Dw(LM, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, "not set") },
            });

            // ---- desktop & boot
            list.Add(new Tweak
            {
                Id = "ui.animations", Section = Section.Performance, Group = "Desktop & boot", Level = Level.Minor,
                Title = "Fewer animations",
                Summary = "Turns off the minimize and maximize animations, taskbar animations and Aero Peek.",
                Gain = "Windows open and close instantly and the desktop feels snappier, especially on older PCs.",
                TradeOff = "Less visual polish. Fully takes effect after you sign out.",
                Affects = A("desktop"),
                After = After.SignOut,
                Presets = GS,
                Actions =
                {
                    Sz(CU, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "\"1\""),
                    Dw(CU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0, "1"),
                    Dw(CU, @"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0, "1"),
                },
            });

            list.Add(new Tweak
            {
                Id = "ui.transparency", Section = Section.Performance, Group = "Desktop & boot", Level = Level.Minor,
                Title = "Turn off transparency effects",
                Summary = "Makes the taskbar, Start menu and window backgrounds solid instead of see-through.",
                Gain = "A little less work for the graphics chip on the desktop. Helps most on laptops with integrated graphics.",
                TradeOff = "Purely visual.",
                Affects = A("desktop"),
                Presets = S,
                Actions = { Dw(CU, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, "1") },
            });

            list.Add(new Tweak
            {
                Id = "ui.menudelay", Section = Section.Performance, Group = "Desktop & boot", Level = Level.Minor,
                Title = "Instant menus",
                Summary = "Cuts the 0.4-second pause before menus and submenus open.",
                Gain = "Right-click menus and submenus appear right away.",
                TradeOff = "Submenus can pop open when your mouse just passes over them.",
                Affects = A("desktop"),
                Presets = All3,
                Actions = { Sz(CU, @"Control Panel\Desktop", "MenuShowDelay", "20", "\"400\"") },
            });

            list.Add(new Tweak
            {
                Id = "boot.startupdelay", Section = Section.Performance, Group = "Desktop & boot", Level = Level.Minor,
                Title = "Start apps right after sign-in",
                Summary = "Removes the delay Windows adds before it launches your startup apps.",
                Gain = "Startup apps are ready sooner after you sign in.",
                TradeOff = "With many startup apps, the first seconds after sign-in can feel busier. Trim them on the Startup apps page.",
                Affects = A("boot"),
                Presets = All3,
                Actions = { Dw(CU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0, "not set") },
            });

            // ---- security trade-offs
            list.Add(new Tweak
            {
                Id = "sec.vbs", Section = Section.Performance, Group = "Security trade-offs", Level = Level.Heavy, Security = true,
                Title = "Turn off Memory Integrity and VBS",
                Summary = "Switches off Core Isolation's Memory Integrity and Virtualization-Based Security, the hypervisor layer Windows 11 runs underneath itself.",
                Gain = "Microsoft lists this as an option for gamers. Benchmarks typically show 2 to 10% more FPS in processor-limited games; the gain depends on your CPU.",
                TradeOff = "Lowers protection against malware that attacks Windows' core and drivers. Some anti-cheat systems and most work or school PCs require it. If your PC enforces VBS through firmware or a policy, Windows may keep it on.",
                Affects = A("fps"),
                After = After.Restart,
                Presets = Preset.None,
                Actions =
                {
                    Dw(LM, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0, "1 on most Windows 11 PCs"),
                    Dw(LM, @"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0, "not set"),
                },
            });
        }

        // ===================================================================================
        // HARDWARE — processor, graphics driver, display and device power behaviour.
        // Power plan values change only the plugged-in (AC) setting; battery behaviour is untouched.
        // ===================================================================================
        static string NoNvidia(Env env) => env.Sys.NvAvailable ? null : "No NVIDIA graphics card or driver found on this PC";

        static void Hardware(List<Tweak> list)
        {
            list.Add(new Tweak
            {
                Id = "cpu.boost", Section = Section.Hardware, Group = "Processor (Intel and AMD)", Level = Level.Minor,
                Title = "Faster CPU boost",
                Summary = "Lets the processor jump to its boost clock sooner and stay there, and tells it to favor speed over saving power.",
                Gain = "Quicker response when a game or app suddenly needs the processor, instead of a short ramp-up.",
                TradeOff = "A bit more power and heat under load. Only the plugged-in setting changes, so battery life isn't affected.",
                Affects = A("fps", "input delay"),
                Presets = GS,
                Actions =
                {
                    new PowerAC(HwIds.SubProcessor, HwIds.BoostMode, 2, "Processor performance boost mode", "Aggressive"),
                    new PowerAC(HwIds.SubProcessor, HwIds.Epp, 0, "Energy performance preference", "0 (maximum performance)"),
                },
            });

            list.Add(new Tweak
            {
                Id = "cpu.parking", Section = Section.Hardware, Group = "Processor (Intel and AMD)", Level = Level.Minor,
                Title = "Keep all CPU cores awake",
                Summary = "Stops Windows from parking (switching off) idle processor cores, so they don't have to wake up when a game spreads its work across them.",
                Gain = "Fewer small stutters when a game's load jumps between cores.",
                TradeOff = "Slightly higher power use at idle. Only the plugged-in setting changes.",
                Affects = A("fps"),
                Presets = GS,
                Unavailable = e => e.Info.IsX3D ? "AMD X3D processors use core parking to keep games on their 3D V-Cache cores, so this is left alone" : null,
                Actions = { new PowerAC(HwIds.SubProcessor, HwIds.CoreParkingMin, 100, "Core parking minimum cores", "100% (no parking)") },
            });

            list.Add(new Tweak
            {
                Id = "cpu.minstate", Section = Section.Hardware, Group = "Processor (Intel and AMD)", Level = Level.Heavy,
                Title = "CPU never clocks down",
                Summary = "Sets the minimum processor state to 100%, so the processor stays at full speed even when the PC is idle.",
                Gain = "No clock ramp-up at all. The difference is small on modern processors, which change speed very quickly anyway.",
                TradeOff = "Noticeably more power use and heat at idle, and possibly louder fans. Only the plugged-in setting changes.",
                Affects = A("input delay"),
                Presets = Preset.None,
                Actions = { new PowerAC(HwIds.SubProcessor, HwIds.MinState, 100, "Minimum processor state", "100%") },
            });

            list.Add(new Tweak
            {
                Id = "nv.power", Section = Section.Hardware, Group = "Graphics card: NVIDIA", Level = Level.Minor,
                Title = "NVIDIA: Prefer maximum performance",
                Summary = "Sets NVIDIA Control Panel's Power management mode to Prefer maximum performance, so the graphics card doesn't drop its clocks in the middle of a game.",
                Gain = "Steadier frame times in games that don't keep the graphics card fully busy all the time.",
                TradeOff = "The card keeps higher clocks whenever an app that uses 3D acceleration is open (that can include browsers and Discord), so it uses more power and runs warmer.",
                Affects = A("fps"),
                Presets = GS,
                Requires = "NVIDIA graphics card",
                Unavailable = NoNvidia,
                Actions = { new NvSetting(HwIds.NvPowerMode, 1, "Power management mode", "Prefer maximum performance") },
            });

            list.Add(new Tweak
            {
                Id = "nv.latency", Section = Section.Hardware, Group = "Graphics card: NVIDIA", Level = Level.Minor,
                Title = "NVIDIA: Low Latency Mode on",
                Summary = "Sets NVIDIA's Low Latency Mode to On, so the processor prepares at most one frame ahead of the graphics card.",
                Gain = "Less input delay when the graphics card is the bottleneck. Games with NVIDIA Reflex use Reflex instead, which works even better.",
                TradeOff = "In some processor-heavy games the average FPS can drop slightly. Games that are already running pick it up after a restart.",
                Affects = A("input delay"),
                Presets = GS,
                Requires = "NVIDIA graphics card",
                Unavailable = NoNvidia,
                Actions = { new NvSetting(HwIds.NvPrerender, 1, "Low Latency Mode (maximum pre-rendered frames)", "On (1 frame)") },
            });

            list.Add(new Tweak
            {
                Id = "nv.refresh", Section = Section.Hardware, Group = "Graphics card: NVIDIA", Level = Level.Minor,
                Title = "NVIDIA: Highest available refresh rate",
                Summary = "Sets NVIDIA Control Panel's Preferred refresh rate to Highest available, so games in exclusive fullscreen use your monitor's fastest rate.",
                Gain = "Many older games start at 60 Hz even on a 144 or 240 Hz monitor. With this they get the full rate, which is smoother and has less input delay.",
                TradeOff = "None in practice. A game that offers its own refresh rate option still uses what you pick there.",
                Affects = A("smoothness", "input delay"),
                Presets = GS,
                Requires = "NVIDIA graphics card",
                Unavailable = NoNvidia,
                Actions = { new NvSetting(HwIds.NvRefresh, 1, "Preferred refresh rate", "Highest available") },
            });

            list.Add(new Tweak
            {
                Id = "nv.shadercache", Section = Section.Hardware, Group = "Graphics card: NVIDIA", Level = Level.Minor,
                Title = "NVIDIA: Unlimited shader cache",
                Summary = "Sets NVIDIA Control Panel's Shader Cache Size to Unlimited, so the driver keeps every shader it has compiled instead of throwing old ones away.",
                Gain = "Fewer \"first time\" stutters when you return to a game or area, because shaders don't have to be compiled again.",
                TradeOff = "The cache can grow to several GB if you play many games. Windows' Disk Cleanup can still empty it (it's the \"DirectX Shader Cache\" entry).",
                Affects = A("stutter"),
                Presets = GS,
                Requires = "NVIDIA graphics card",
                Unavailable = NoNvidia,
                Actions = { new NvSetting(HwIds.NvShaderCache, 0xFFFFFFFF, "Shader Cache Size", "Unlimited") },
            });

            list.Add(new Tweak
            {
                Id = "nv.texture", Section = Section.Hardware, Group = "Graphics card: NVIDIA", Level = Level.Minor,
                Title = "NVIDIA: Texture filtering for performance",
                Summary = "Sets Texture filtering - Quality to High performance and turns on Anisotropic sample optimization.",
                Gain = "Usually 1 to 3% more FPS, more in older or texture-heavy games.",
                TradeOff = "Textures can look a little softer or shimmer slightly at steep angles. Undo it if you notice.",
                Affects = A("fps"),
                Presets = S,
                Requires = "NVIDIA graphics card",
                Unavailable = NoNvidia,
                Actions =
                {
                    new NvSetting(HwIds.NvTexQuality, 0x14, "Texture filtering - Quality", "High performance"),
                    new NvSetting(HwIds.NvAnisoOpt, 1, "Texture filtering - Anisotropic sample optimization", "On"),
                },
            });

            list.Add(new Tweak
            {
                Id = "display.refresh", Section = Section.Hardware, Group = "Display", Level = Level.Minor,
                Title = "Run displays at their highest refresh rate",
                Summary = "Checks every display and switches any that runs below its fastest refresh rate, for example a 144 Hz monitor stuck at 60 Hz.",
                Gain = "If a display was running slower than it can, this is the biggest smoothness upgrade on this page: frames show up sooner and motion looks clearer.",
                TradeOff = "None if your monitor and cable support it. You confirm the new rate on screen; if you don't, it goes back by itself after 15 seconds.",
                Affects = A("smoothness", "input delay"),
                Presets = All3,
                Actions = { new DisplayMaxRefresh() },
            });

            list.Add(new Tweak
            {
                Id = "dev.usb", Section = Section.Hardware, Group = "Devices", Level = Level.Minor,
                Title = "USB devices never sleep",
                Summary = "Turns off USB selective suspend, so Windows doesn't put your mouse, keyboard, headset or controller to sleep to save power.",
                Gain = "No wake-up delay and no random disconnects from USB devices.",
                TradeOff = "Slightly higher power use. Only the plugged-in setting changes.",
                Affects = A("input"),
                Presets = GS,
                Actions = { new PowerAC(HwIds.SubUsb, HwIds.UsbSuspend, 0, "USB selective suspend", "Disabled") },
            });

            list.Add(new Tweak
            {
                Id = "dev.pcie", Section = Section.Hardware, Group = "Devices", Level = Level.Minor,
                Title = "PCI Express power saving off",
                Summary = "Turns off Link State Power Management, which lets the connection to your graphics card and NVMe drive drop into a low-power state.",
                Gain = "Removes a small wake-up delay on the graphics card and SSD connection.",
                TradeOff = "Slightly higher power use. Only the plugged-in setting changes.",
                Affects = A("input delay"),
                Presets = GS,
                Actions = { new PowerAC(HwIds.SubPcie, HwIds.Aspm, 0, "PCI Express link state power management", "Off") },
            });

            list.Add(new Tweak
            {
                Id = "dev.wifi", Section = Section.Hardware, Group = "Devices", Level = Level.Minor,
                Title = "Wi-Fi at full power",
                Summary = "Sets the wireless adapter's power saving mode to Maximum Performance while the PC is plugged in.",
                Gain = "Fewer ping spikes on Wi-Fi, because the adapter doesn't nap between packets. No effect on wired connections.",
                TradeOff = "A little more power use. Only the plugged-in setting changes.",
                Affects = A("network delay"),
                Presets = GS,
                Actions = { new PowerAC(HwIds.SubWifi, HwIds.WifiPower, 0, "Wireless adapter power saving mode", "Maximum Performance") },
            });

            list.Add(new Tweak
            {
                Id = "dev.disk", Section = Section.Hardware, Group = "Devices", Level = Level.Minor,
                Title = "Drives never power down",
                Summary = "Sets \"Turn off hard disk after\" to Never while the PC is plugged in.",
                Gain = "No pause while a sleeping hard drive spins back up in the middle of a game. SSDs are barely affected.",
                TradeOff = "Hard drives keep spinning while the PC is on, using a little more power. Only the plugged-in setting changes.",
                Affects = A("stutter"),
                Presets = GS,
                Actions = { new PowerAC(HwIds.SubDisk, HwIds.DiskIdle, 0, "Turn off hard disk after", "Never") },
            });
        }

        static List<TweakAction> NagleActions(Env env)
        {
            var acts = new List<TweakAction>();
            const string ifs = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
            try
            {
                foreach (var g in env.Reg.SubKeyNames(LM, ifs))
                {
                    var p = ifs + "\\" + g;
                    var dhcp = env.Reg.Get(LM, p, "DhcpIPAddress")?.Data as string;
                    var ip = env.Reg.Get(LM, p, "IPAddress")?.Data as string[];
                    bool connected = (!string.IsNullOrEmpty(dhcp) && dhcp != "0.0.0.0")
                                     || (ip != null && ip.Any(x => !string.IsNullOrEmpty(x) && x != "0.0.0.0"));
                    if (!connected) continue;
                    acts.Add(Dw(LM, p, "TcpAckFrequency", 1, "not set"));
                    acts.Add(Dw(LM, p, "TCPNoDelay", 1, "not set"));
                }
            }
            catch { }
            return acts;
        }

        // ===================================================================================
        // DEBLOAT
        // ===================================================================================
        static void Debloat(List<Tweak> list, Env env)
        {
            const string cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
            const string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

            list.Add(new Tweak
            {
                Id = "priv.telemetry", Section = Section.Debloat, Group = "Telemetry & tracking", Level = Level.Minor,
                Title = "Minimize diagnostic data",
                Summary = "Sets diagnostic data to the lowest level your edition of Windows allows and switches off the service that sends it.",
                Gain = "Less data collection, upload traffic and disk activity in the background.",
                TradeOff = "Don't use this if you're in the Windows Insider program, which needs diagnostic data. Settings will show \"Some settings are managed by your organization\"; that only refers to this policy.",
                Affects = A("privacy", "processes"),
                Presets = All3,
                Actions =
                {
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, "not set"),
                    new ServiceStart("DiagTrack", "Connected User Experiences and Telemetry", 4, stopNow: true),
                },
            });

            list.Add(new Tweak
            {
                Id = "priv.tasks", Section = Section.Debloat, Group = "Telemetry & tracking", Level = Level.Minor,
                Title = "Turn off data-collection tasks",
                Summary = "Disables the scheduled tasks that wake up to gather compatibility and usage data.",
                Gain = "Removes periodic bursts of processor and disk use. The Compatibility Appraiser task is a known cause of random slowdowns.",
                TradeOff = "None for everyday use.",
                Affects = A("privacy", "processes"),
                Presets = All3,
                Actions =
                {
                    new TaskOff(@"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser"),
                    new TaskOff(@"\Microsoft\Windows\Application Experience\ProgramDataUpdater"),
                    new TaskOff(@"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator"),
                    new TaskOff(@"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip"),
                    new TaskOff(@"\Microsoft\Windows\Autochk\Proxy"),
                    new TaskOff(@"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector"),
                    new TaskOff(@"\Microsoft\Windows\Feedback\Siuf\DmClient"),
                    new TaskOff(@"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload"),
                },
            });

            list.Add(new Tweak
            {
                Id = "priv.ads", Section = Section.Debloat, Group = "Telemetry & tracking", Level = Level.Minor,
                Title = "Advertising ID and tailored offers off",
                Summary = "Stops apps from using an advertising ID to profile you, and stops Microsoft from using your diagnostic data for personalized tips and offers.",
                Gain = "Fewer personalized ads in apps. Websites can no longer read your list of languages.",
                TradeOff = "Ads don't disappear; they just stop being based on your profile.",
                Affects = A("privacy"),
                Presets = All3,
                Actions =
                {
                    Dw(CU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, "1"),
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1, "not set"),
                    Dw(CU, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, "1"),
                    Dw(CU, @"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", 1, "not set"),
                },
            });

            list.Add(new Tweak
            {
                Id = "priv.activity", Section = Section.Debloat, Group = "Telemetry & tracking", Level = Level.Minor,
                Title = "Activity history off",
                Summary = "Stops Windows from keeping a timeline of the apps, files and websites you open.",
                Gain = "Less tracking and less writing to disk in the background.",
                TradeOff = "\"Pick up where you left off\" between devices stops working.",
                Affects = A("privacy"),
                Presets = All3,
                Actions =
                {
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0, "not set"),
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, "not set"),
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, "not set"),
                },
            });

            list.Add(new Tweak
            {
                Id = "priv.feedback", Section = Section.Debloat, Group = "Telemetry & tracking", Level = Level.Minor,
                Title = "No feedback requests",
                Summary = "Stops the \"How likely are you to recommend Windows?\" style pop-ups.",
                Gain = "One less interruption.",
                TradeOff = "None. You can still send feedback yourself through Feedback Hub.",
                Affects = A("privacy"),
                Presets = All3,
                Actions =
                {
                    Dw(CU, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, "not set"),
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", 1, "not set"),
                },
            });

            list.Add(new Tweak
            {
                Id = "priv.typing", Section = Section.Debloat, Group = "Telemetry & tracking", Level = Level.Minor,
                Title = "Typing and inking data off",
                Summary = "Stops Windows from collecting what you type and write by hand to build a personal dictionary and send improvement data to Microsoft.",
                Gain = "Less personal data collected and uploaded.",
                TradeOff = "Text suggestions and handwriting recognition learn less from you over time.",
                Affects = A("privacy"),
                Presets = All3,
                Actions =
                {
                    Dw(CU, @"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1, "0"),
                    Dw(CU, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1, "0"),
                    Dw(CU, @"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0, "1"),
                    Dw(CU, @"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0, "1"),
                },
            });

            list.Add(new Tweak
            {
                Id = "priv.recent", Section = Section.Debloat, Group = "Telemetry & tracking", Level = Level.Minor,
                Title = "Don't track the apps and files you open",
                Summary = "Stops Windows from keeping lists of the apps you start and the files you open, for Start and jump lists.",
                Gain = "Nobody using your PC sees what you've opened recently.",
                TradeOff = "Start's \"Most used\" list, jump lists and recent files in File Explorer stay empty, so reopening recent things takes an extra click.",
                Affects = A("privacy"),
                After = After.Explorer,
                Presets = S,
                Actions =
                {
                    Dw(CU, adv, "Start_TrackProgs", 0, "1"),
                    Dw(CU, adv, "Start_TrackDocs", 0, "1"),
                },
            });

            list.Add(new Tweak
            {
                Id = "priv.wer", Section = Section.Debloat, Group = "Telemetry & tracking", Level = Level.Minor,
                Title = "Windows Error Reporting off",
                Summary = "Stops Windows from collecting crash data and sending it to Microsoft after an app crashes.",
                Gain = "No WerFault.exe gathering memory dumps after a crash, which can freeze a game for seconds.",
                TradeOff = "Microsoft can't use your crash reports to fix bugs, and Windows stops suggesting solutions for problems.",
                Affects = A("privacy", "processes"),
                Presets = S,
                Actions =
                {
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting", "Disabled", 1, "not set"),
                    Dw(LM, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1, "0"),
                },
            });

            // ---- ads & suggestions
            list.Add(new Tweak
            {
                Id = "ads.suggestions", Section = Section.Debloat, Group = "Ads, tips & suggestions", Level = Level.Minor,
                Title = "No tips, suggestions or ads in Windows",
                Summary = "Turns off suggested apps in Start, tips, the \"Get even more out of Windows\" screens, suggestions in Settings, sync ads in File Explorer and \"fun facts\" on the lock screen.",
                Gain = "A quieter Windows with fewer pop-ups and less promoted content.",
                TradeOff = "None. Windows Spotlight wallpapers keep working.",
                Affects = A("privacy", "desktop"),
                Presets = All3,
                Actions =
                {
                    Dw(CU, cdm, "SubscribedContent-338388Enabled", 0, "1"),
                    Dw(CU, cdm, "SubscribedContent-338389Enabled", 0, "1"),
                    Dw(CU, cdm, "SubscribedContent-338393Enabled", 0, "1"),
                    Dw(CU, cdm, "SubscribedContent-353694Enabled", 0, "1"),
                    Dw(CU, cdm, "SubscribedContent-353696Enabled", 0, "1"),
                    Dw(CU, cdm, "SubscribedContent-310093Enabled", 0, "1"),
                    Dw(CU, cdm, "SubscribedContent-338387Enabled", 0, "1"),
                    Dw(CU, cdm, "RotatingLockScreenOverlayEnabled", 0, "1"),
                    Dw(CU, cdm, "SystemPaneSuggestionsEnabled", 0, "1"),
                    Dw(CU, cdm, "SoftLandingEnabled", 0, "1"),
                    Dw(CU, @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0, "1"),
                    Dw(CU, adv, "ShowSyncProviderNotifications", 0, "1"),
                    Dw(CU, adv, "Start_IrisRecommendations", 0, "1"),
                },
            });

            list.Add(new Tweak
            {
                Id = "ads.autoinstall", Section = Section.Debloat, Group = "Ads, tips & suggestions", Level = Level.Minor,
                Title = "Stop automatic installs of sponsored apps",
                Summary = "Stops Windows from quietly installing promoted apps and games, the ones that suddenly show up in Start after an update.",
                Gain = "No new Candy Crush-style apps appearing on their own.",
                TradeOff = "None.",
                Affects = A("disk", "desktop"),
                Presets = All3,
                Actions =
                {
                    Dw(CU, cdm, "SilentInstalledAppsEnabled", 0, "1"),
                    Dw(CU, cdm, "PreInstalledAppsEnabled", 0, "1"),
                    Dw(CU, cdm, "PreInstalledAppsEverEnabled", 0, "1"),
                    Dw(CU, cdm, "OemPreInstalledAppsEnabled", 0, "1"),
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1, "not set"),
                },
            });

            // ---- start, search & AI
            list.Add(new Tweak
            {
                Id = "search.web", Section = Section.Debloat, Group = "Start, search & AI", Level = Level.Minor,
                Title = "No web results in Start search",
                Summary = "Start search only looks through your PC, instead of also sending what you type to Bing.",
                Gain = "Faster, cleaner results, and your keystrokes stay on your PC.",
                TradeOff = "No web suggestions in Start. File Explorer's search box stops listing recent searches.",
                Affects = A("privacy", "desktop"),
                After = After.Explorer,
                Presets = All3,
                Actions =
                {
                    Dw(CU, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, "not set"),
                    Dw(CU, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0, "not set"),
                },
            });

            list.Add(new Tweak
            {
                Id = "search.highlights", Section = Section.Debloat, Group = "Start, search & AI", Level = Level.Minor,
                Title = "No search highlights",
                Summary = "Removes the illustrated \"highlights\" (holidays, trending searches, news) from the search box and search panel.",
                Gain = "A plain search box, and the search panel opens without loading web content first.",
                TradeOff = "None.",
                Affects = A("desktop", "privacy"),
                After = After.Explorer,
                Presets = All3,
                Actions = { Dw(CU, @"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0, "1") },
            });

            list.Add(new Tweak
            {
                Id = "ai.copilot", Section = Section.Debloat, Group = "Start, search & AI", Level = Level.Minor,
                Title = "Copilot off",
                Summary = "Turns off Windows Copilot and hides its taskbar button. To remove the Copilot app itself, tick it under Preinstalled apps.",
                Gain = "No Copilot button or Copilot shortcut taking up space.",
                TradeOff = "Copilot isn't available in Windows until you undo this.",
                Affects = A("desktop"),
                After = After.Explorer,
                Presets = S,
                Actions =
                {
                    Dw(CU, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, "not set"),
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, "not set"),
                    Dw(CU, adv, "ShowCopilotButton", 0, "1"),
                },
            });

            list.Add(new Tweak
            {
                Id = "ai.recall", Section = Section.Debloat, Group = "Start, search & AI", Level = Level.Minor,
                Title = "Recall snapshots off",
                Summary = "Stops Recall from saving snapshots of your screen. Only Copilot+ PCs have Recall; on other PCs this does nothing.",
                Gain = "Nothing on your screen gets saved and indexed.",
                TradeOff = "You can't search your screen history.",
                Affects = A("privacy"),
                Presets = All3,
                Unavailable = Win11Only, Requires = "Windows 11",
                Actions =
                {
                    Dw(CU, @"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, "not set"),
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1, "not set"),
                },
            });

            list.Add(new Tweak
            {
                Id = "ui.widgets", Section = Section.Debloat, Group = "Start, search & AI", Level = Level.Minor,
                Title = "Widgets off",
                Summary = "Removes the Widgets board (news, weather, stocks) from the taskbar and stops it running in the background.",
                Gain = "Fewer processes: Widgets.exe and the web views it keeps open.",
                TradeOff = "No weather or news on the taskbar.",
                Affects = A("processes", "ram"),
                After = After.Explorer,
                Presets = GS,
                Actions =
                {
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0, "not set"),
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Windows\Windows Feeds", "EnableFeeds", 0, "not set"),
                },
            });

            // ---- edge & onedrive
            list.Add(new Tweak
            {
                Id = "edge.background", Section = Section.Debloat, Group = "Edge & OneDrive", Level = Level.Minor,
                Title = "Edge stops running after you close it",
                Summary = "Turns off Edge's Startup Boost and background mode, which keep Edge running even when no Edge window is open.",
                Gain = "Frees memory and removes several msedge.exe processes.",
                TradeOff = "Edge opens a moment slower. Edge's settings will say \"Your browser is managed by your organization\"; that only refers to these two settings.",
                Affects = A("processes", "ram"),
                Presets = All3,
                Actions =
                {
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0, "not set"),
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0, "not set"),
                },
            });

            list.Add(new Tweak
            {
                Id = "edge.extras", Section = Section.Debloat, Group = "Edge & OneDrive", Level = Level.Minor,
                Title = "No Edge sidebar or shopping pop-ups",
                Summary = "Turns off Edge's sidebar (the Copilot and apps bar on the right) and the shopping assistant that pops up coupon and price offers.",
                Gain = "A cleaner browser and fewer pop-ups while you shop.",
                TradeOff = "Those two Edge features are unavailable until you undo this.",
                Affects = A("desktop", "privacy"),
                Presets = All3,
                Actions =
                {
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Edge", "HubsSidebarEnabled", 0, "not set"),
                    Dw(LM, @"SOFTWARE\Policies\Microsoft\Edge", "EdgeShoppingAssistantEnabled", 0, "not set"),
                },
            });

            list.Add(new Tweak
            {
                Id = "onedrive.autostart", Section = Section.Debloat, Group = "Edge & OneDrive", Level = Level.Minor,
                Title = "Don't start OneDrive with Windows",
                Summary = "Keeps OneDrive installed but stops it from launching when you sign in. Same as switching it off in Task Manager › Startup apps.",
                Gain = "One less app running from the moment you sign in.",
                TradeOff = "Files only sync while OneDrive is open. Start it from the Start menu when you need it.",
                Affects = A("boot", "processes"),
                Presets = Preset.None,
                Unavailable = e => e.Reg.Get(CU, @"Software\Microsoft\Windows\CurrentVersion\Run", "OneDrive") == null ? "OneDrive isn't set to start with Windows" : null,
                Actions = { new StartupOff(CU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "OneDrive", "OneDrive") },
            });

            list.Add(new Tweak
            {
                Id = "onedrive.remove", Section = Section.Debloat, Group = "Edge & OneDrive", Level = Level.Heavy,
                Title = "Uninstall OneDrive",
                Summary = "Runs OneDrive's own uninstaller. Your files stay in your OneDrive folder on this PC and online.",
                Gain = "Removes OneDrive.exe and its background updater.",
                TradeOff = "Files stop syncing. Files marked \"online-only\" (cloud icon) are not on this PC: download any you need first.",
                UndoNote = "Simplemize can't undo this. Reinstall OneDrive for free from microsoft.com/onedrive or the Microsoft Store.",
                StoreQuery = "OneDrive",
                Affects = A("processes", "ram"),
                Presets = Preset.None,
                Unavailable = e => e.Sys.OneDriveUninstallCommand() == null ? "OneDrive isn't installed" : OneDriveFolderBackup(e),
                Actions = { new OneDriveRemove() },
            });

            // ---- preinstalled apps
            foreach (var app in Apps)
            {
                var pattern = app[0];
                var t = new Tweak
                {
                    Id = "app." + pattern.Replace("*", "").Trim('.').ToLowerInvariant(),
                    Section = Section.Debloat,
                    Group = "Preinstalled apps",
                    Level = Level.Heavy,
                    Title = app[1],
                    Summary = app[2],
                    Gain = "One less app in Start, and no background updates or tasks for it.",
                    TradeOff = "Uninstalled for every account on this PC.",
                    UndoNote = "Simplemize can't put it back. Reinstall it for free from the Microsoft Store.",
                    StoreQuery = app[1],
                    Affects = A("disk"),
                    Presets = app[3] == "y" ? S : Preset.None,
                    Actions = { new AppRemove(pattern) },
                };
                var remover = (AppRemove)t.Actions[0];
                t.Unavailable = e => e.Apps == null ? "Checking installed apps…" : remover.Installed(e) ? null : "Not installed";
                list.Add(t);
            }
        }

        static string OneDriveFolderBackup(Env env)
        {
            foreach (var n in new[] { "Desktop", "Personal", "My Pictures" })
            {
                var v = env.Reg.Get(CU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", n)?.Data as string;
                if (v != null && Environment.ExpandEnvironmentVariables(v).IndexOf("OneDrive", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Your Desktop, Documents or Pictures folder is inside OneDrive (folder backup is on). Turn off folder backup in OneDrive's settings first so nothing goes missing.";
            }
            return null;
        }

        // pattern, name, description, in "Stripped" preset
        static readonly string[][] Apps =
        {
            new[] { "Microsoft.BingNews", "Microsoft News", "News feed app.", "y" },
            new[] { "Microsoft.BingWeather", "Weather", "MSN Weather app. The weather on the taskbar comes from Widgets, not this app.", "y" },
            new[] { "Microsoft.BingSearch", "Bing Search", "Adds Bing web results to Windows search.", "y" },
            new[] { "Microsoft.GetHelp", "Get Help", "Microsoft's support chat app.", "y" },
            new[] { "Microsoft.Getstarted", "Tips", "The Windows tips app.", "y" },
            new[] { "Microsoft.MicrosoftSolitaireCollection", "Solitaire & Casual Games", "Card games with ads and a subscription.", "y" },
            new[] { "Microsoft.People", "People", "Old contacts app.", "y" },
            new[] { "Microsoft.WindowsFeedbackHub", "Feedback Hub", "Sends feedback and bug reports to Microsoft.", "y" },
            new[] { "Microsoft.ZuneVideo", "Movies & TV", "Microsoft's old video store and player. Media Player is not affected.", "y" },
            new[] { "Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office) hub", "A launcher that advertises Microsoft 365. Installed Office apps are not affected.", "y" },
            new[] { "Clipchamp.Clipchamp", "Clipchamp", "Online video editor.", "y" },
            new[] { "Microsoft.549981C3F5F10", "Cortana", "The retired voice assistant.", "y" },
            new[] { "Microsoft.WindowsMaps", "Maps", "Offline maps app.", "y" },
            new[] { "Microsoft.PowerAutomateDesktop", "Power Automate", "Desktop automation tool.", "y" },
            new[] { "Microsoft.SkypeApp", "Skype", "Skype, which Microsoft has shut down.", "y" },
            new[] { "MicrosoftTeams", "Teams (personal chat)", "The old built-in Teams chat for personal accounts.", "y" },
            new[] { "Microsoft.Copilot", "Copilot app", "The Copilot app.", "y" },
            new[] { "Microsoft.Windows.DevHome", "Dev Home", "Developer dashboard that Microsoft has discontinued.", "y" },
            new[] { "Microsoft.MixedReality.Portal", "Mixed Reality Portal", "Only needed for Windows Mixed Reality headsets.", "y" },
            new[] { "Microsoft.Microsoft3DViewer", "3D Viewer", "Viewer for 3D models.", "y" },
            new[] { "Microsoft.MSPaint", "Paint 3D", "Paint 3D. The regular Paint app is not affected.", "y" },
            new[] { "Microsoft.Print3D", "Print 3D", "3D printing app.", "y" },
            new[] { "Microsoft.OneConnect", "Mobile Plans", "Buys mobile data for PCs with a SIM slot.", "y" },
            new[] { "Microsoft.Messaging", "Messaging", "Old SMS app.", "y" },
            new[] { "Microsoft.Wallet", "Wallet", "Old payments app.", "y" },
            new[] { "Microsoft.XboxApp", "Xbox Console Companion", "The old Xbox app. The new Xbox app and Game Bar are not touched.", "y" },
            new[] { "MicrosoftCorporationII.MicrosoftFamily", "Family Safety", "Parental controls app. Only remove it if nobody uses Family Safety on this PC.", "y" },
            new[] { "king.com.*", "Candy Crush and other King games", "Sponsored games that came with Windows.", "y" },
            new[] { "5A894077.McAfeeSecurity", "McAfee trial", "Sponsored antivirus trial. Windows Security keeps protecting you.", "y" },
            new[] { "Microsoft.Todos", "Microsoft To Do", "Task list app. Keep it if you use To Do.", "n" },
            new[] { "Microsoft.YourPhone", "Phone Link", "Connects your Android phone or iPhone to the PC.", "n" },
            new[] { "Microsoft.OutlookForWindows", "Outlook (new)", "The new Outlook / Mail app. Classic Outlook from Office is not affected.", "n" },
            new[] { "microsoft.windowscommunicationsapps", "Mail and Calendar (old)", "The retired Mail and Calendar apps.", "n" },
            new[] { "MSTeams", "Microsoft Teams", "The current Teams app. This is also the app for work or school Teams.", "n" },
            new[] { "SpotifyAB.SpotifyMusic", "Spotify", "Music streaming app. Keep it if you use it.", "n" },
            new[] { "Disney.37853FA22FAF4", "Disney+", "Streaming app.", "n" },
            new[] { "4DF9E0F8.Netflix", "Netflix", "Streaming app.", "n" },
            new[] { "AmazonVideo.PrimeVideo", "Prime Video", "Streaming app.", "n" },
            new[] { "BytedancePte.Ltd.TikTok", "TikTok", "Social media app.", "n" },
            new[] { "Facebook.Facebook", "Facebook", "Social media app.", "n" },
            new[] { "Facebook.InstagramBeta", "Instagram", "Social media app.", "n" },
            new[] { "7EE7776C.LinkedInforWindows", "LinkedIn", "Social media app.", "n" },
        };

        // ===================================================================================
        // PROCESSES
        // ===================================================================================
        static void Processes(List<Tweak> list)
        {
            list.Add(new Tweak
            {
                Id = "svc.manual", Section = Section.Processes, Group = "Services", Level = Level.Minor,
                Title = "Start rarely used services only when needed",
                Summary = "Switches a handful of services from Automatic to Manual. Windows still starts each one the moment something needs it.",
                Gain = "A slightly quicker boot and a few fewer processes after startup.",
                TradeOff = "None in practice: nothing is switched off.",
                Affects = A("processes", "boot"),
                Presets = All3,
                Actions =
                {
                    new ServiceStart("MapsBroker", "Downloaded Maps Manager", 3, lowerOnly: true),
                    new ServiceStart("TrkWks", "Distributed Link Tracking Client", 3, lowerOnly: true),
                    new ServiceStart("PcaSvc", "Program Compatibility Assistant", 3, lowerOnly: true),
                    new ServiceStart("edgeupdate", "Microsoft Edge Update", 3, lowerOnly: true),
                    new ServiceStart("gupdate", "Google Update", 3, lowerOnly: true),
                },
            });

            list.Add(new Tweak
            {
                Id = "svc.sysmain", Section = Section.Processes, Group = "Services", Level = Level.Heavy,
                Title = "Turn off SysMain (Superfetch)",
                Summary = "Stops Windows from preloading your most-used apps into memory in the background.",
                Gain = "Less background disk and processor activity and more free memory. On an SSD, apps open just about as fast without it.",
                TradeOff = "Apps you use a lot can open a little slower. Leave it on if Windows is installed on a hard drive (HDD).",
                Affects = A("ram", "disk", "processes"),
                Presets = S,
                Warning = e => e.Info.SystemOnHdd == true ? "Windows is on a hard drive (HDD) here. SysMain helps a lot on hard drives, so it's better left on." : null,
                Actions = { new ServiceStart("SysMain", "SysMain", 4, stopNow: true) },
            });

            list.Add(new Tweak
            {
                Id = "svc.search", Section = Section.Processes, Group = "Services", Level = Level.Heavy,
                Title = "Turn off search indexing",
                Summary = "Switches off the Windows Search service, which keeps scanning your files to build a search index.",
                Gain = "No more SearchIndexer.exe using the disk and processor in the background.",
                TradeOff = "Searching in Start and File Explorer still works, but it's slower and can't look inside documents. Search in classic Outlook gets slow or incomplete.",
                Affects = A("processes", "disk"),
                Presets = S,
                Actions = { new ServiceStart("WSearch", "Windows Search", 4, stopNow: true) },
            });

            list.Add(new Tweak
            {
                Id = "svc.spooler", Section = Section.Processes, Group = "Services", Level = Level.Heavy,
                Title = "Turn off printing (Print Spooler)",
                Summary = "Switches off the Print Spooler service. Only for PCs that never print.",
                Gain = "One less service running all the time. It also shuts a service that has had serious security holes in the past.",
                TradeOff = "Nothing can print, not even \"Microsoft Print to PDF\". Undo it before you need to print.",
                Affects = A("processes"),
                Presets = Preset.None,
                Actions = { new ServiceStart("Spooler", "Print Spooler", 4, stopNow: true) },
            });

            list.Add(new Tweak
            {
                Id = "svc.grouping", Section = Section.Processes, Group = "Services", Level = Level.Heavy,
                Title = "Group services into fewer processes",
                Summary = "Lets Windows run related services together inside shared svchost.exe processes, instead of giving almost every service its own.",
                Gain = "Task Manager typically shows 40 to 70 fewer processes, and memory use drops a little.",
                TradeOff = "Windows separates services for stability and security. Grouped together, one crashing service can take its neighbors down with it. Takes effect after a restart.",
                Affects = A("processes", "ram"),
                After = After.Restart,
                Presets = S,
                Actions = { SvcHostThreshold() },
            });

            list.Add(new Tweak
            {
                Id = "apps.background", Section = Section.Processes, Group = "Background activity", Level = Level.Minor,
                Title = "Stop Store apps running in the background",
                Summary = "Prevents apps from the Microsoft Store from running while you're not using them. Regular desktop programs aren't affected.",
                Gain = "Fewer background processes, and less battery and network use.",
                TradeOff = "Store apps (WhatsApp or Phone Link, for example) can't show notifications or sync while they're closed.",
                Affects = A("processes", "battery"),
                Presets = S,
                Actions =
                {
                    Dw(CU, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1, "0"),
                    Dw(CU, @"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 0, "1"),
                },
            });
        }

        static RegSet SvcHostThreshold()
        {
            var mem = new Native.MEMORYSTATUSEX();
            long kb = Native.GlobalMemoryStatusEx(mem) ? (long)(mem.ullTotalPhys / 1024) + 1024 : 0x380000;
            if (kb > uint.MaxValue) kb = uint.MaxValue;
            return Dw(LM, @"SYSTEM\CurrentControlSet\Control", "SvcHostSplitThresholdInKB", unchecked((int)(uint)kb), "3670016 (3.5 GB)");
        }

        // ===================================================================================
        // CLEANUP — one-time jobs
        // ===================================================================================
        static string Win(string rel) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), rel);
        static string PData(string rel) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), rel);
        static string Local(string rel) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), rel);

        static void Cleanup(List<Tweak> list)
        {
            var day = TimeSpan.FromHours(24);
            string userTemp = Path.GetTempPath();
            string winTemp = Win("Temp");
            string wu = Win(@"SoftwareDistribution\Download");
            string[] doCache = { Win(@"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"), Win(@"SoftwareDistribution\DeliveryOptimization") };
            string[] reports = { PData(@"Microsoft\Windows\WER\ReportArchive"), PData(@"Microsoft\Windows\WER\ReportQueue"), Local("CrashDumps") };

            list.Add(new Tweak
            {
                Id = "clean.temp", Section = Section.Cleanup, Group = "Files", Level = Level.Minor, OneShot = true,
                Title = "Temporary files",
                Summary = "Deletes leftover files older than a day from your temp folder and the Windows temp folder.",
                Gain = "Free disk space. Files from an installer that's running right now are left alone.",
                TradeOff = "None. Files that are in use are skipped.",
                Affects = A("disk"),
                Presets = All3,
                Measure = e => SystemOps.Measure(userTemp, day) + SystemOps.Measure(winTemp, day),
                Actions =
                {
                    new Job(userTemp + "  \u00B7  " + winTemp, "delete files older than 24 hours (files in use are skipped)",
                        e => "freed " + Fmt.Bytes(e.Sys.Clean(userTemp, day) + e.Sys.Clean(winTemp, day))),
                },
            });

            list.Add(new Tweak
            {
                Id = "clean.wu", Section = Section.Cleanup, Group = "Files", Level = Level.Minor, OneShot = true,
                Title = "Windows Update download cache",
                Summary = "Deletes update files Windows has already downloaded. Installed updates are not affected.",
                Gain = "Often frees one to several GB. It can also unstick an update that keeps failing.",
                TradeOff = "An update that was waiting to install downloads again.",
                Affects = A("disk"),
                Presets = GS,
                Measure = e => SystemOps.Measure(wu, TimeSpan.Zero),
                Actions =
                {
                    new Job(wu, "stop Windows Update, delete the downloads, start it again", e =>
                    {
                        if (e.Sys.IsServiceRunning("TrustedInstaller"))
                            throw new InvalidOperationException("Windows is installing updates right now. Try again once it's done.");
                        bool wasRunning = e.Sys.IsServiceRunning("wuauserv");
                        e.Sys.StopService("wuauserv");
                        long freed = e.Sys.Clean(wu, TimeSpan.Zero);
                        if (wasRunning) e.Sys.StartService("wuauserv");
                        return "freed " + Fmt.Bytes(freed);
                    }),
                },
            });

            list.Add(new Tweak
            {
                Id = "clean.do", Section = Section.Cleanup, Group = "Files", Level = Level.Minor, OneShot = true,
                Title = "Delivery Optimization cache",
                Summary = "Deletes the update files Windows keeps around to share with other PCs.",
                Gain = "Free disk space.",
                TradeOff = "None.",
                Affects = A("disk"),
                Presets = GS,
                Measure = e => doCache.Sum(d => SystemOps.Measure(d, TimeSpan.Zero)),
                Actions =
                {
                    new Job("PowerShell", "Delete-DeliveryOptimizationCache -Force", e =>
                    {
                        long before = doCache.Sum(d => SystemOps.Measure(d, TimeSpan.Zero));
                        int rc = e.Sys.RunChange(SystemOps.PowerShellPath, "-NoProfile -NonInteractive -Command \"Delete-DeliveryOptimizationCache -Force\"", out _);
                        if (rc != 0) throw new InvalidOperationException("Windows couldn't clear the cache (exit " + rc + ").");
                        return "freed " + Fmt.Bytes(e.Sandbox ? before : Math.Max(0, before - doCache.Sum(d => SystemOps.Measure(d, TimeSpan.Zero))));
                    }),
                },
            });

            list.Add(new Tweak
            {
                Id = "clean.reports", Section = Section.Cleanup, Group = "Files", Level = Level.Minor, OneShot = true,
                Title = "Error reports and crash dumps",
                Summary = "Deletes old Windows error reports and app crash dumps.",
                Gain = "Free disk space.",
                TradeOff = "If you're troubleshooting a crash with someone, send them the dumps first.",
                Affects = A("disk"),
                Presets = All3,
                Measure = e => reports.Sum(d => SystemOps.Measure(d, TimeSpan.Zero)),
                Actions = { new Job(string.Join("  ·  ", reports), "delete all files", e => "freed " + Fmt.Bytes(reports.Sum(d => e.Sys.Clean(d, TimeSpan.Zero)))) },
            });

            list.Add(new Tweak
            {
                Id = "clean.dns", Section = Section.Cleanup, Group = "Network", Level = Level.Minor, OneShot = true,
                Title = "Flush DNS cache",
                Summary = "Clears the list of website addresses Windows remembers, so it looks them up fresh.",
                Gain = "Fixes \"site can't be reached\" errors after a website or game server moves. It doesn't make your connection faster.",
                TradeOff = "None.",
                Affects = A("network"),
                Presets = All3,
                Actions =
                {
                    new Job("ipconfig", "/flushdns", e =>
                    {
                        int rc = e.Sys.RunChange(Path.Combine(Environment.SystemDirectory, "ipconfig.exe"), "/flushdns", out _);
                        if (rc != 0) throw new InvalidOperationException("ipconfig returned " + rc);
                        return "done";
                    }),
                },
            });

            list.Add(new Tweak
            {
                Id = "clean.components", Section = Section.Cleanup, Group = "Windows", Level = Level.Minor, OneShot = true, Slow = true,
                Title = "Old Windows update components",
                Summary = "Runs Windows' own component cleanup (DISM), which removes old versions of system files that updates replaced.",
                Gain = "Frees anywhere from a few hundred MB to several GB.",
                TradeOff = "Takes 5 to 15 minutes. You can still uninstall recent updates afterwards: the /ResetBase option, which would prevent that, is not used.",
                Affects = A("disk"),
                Presets = S,
                Actions =
                {
                    new Job("DISM", "/Online /Cleanup-Image /StartComponentCleanup", e =>
                    {
                        int rc = e.Sys.RunChange(Path.Combine(Environment.SystemDirectory, "Dism.exe"), "/Online /Cleanup-Image /StartComponentCleanup /NoRestart", out _, 3600000);
                        if (rc != 0 && rc != 3010) throw new InvalidOperationException("DISM returned " + rc + ". Details are in C:\\Windows\\Logs\\DISM\\dism.log.");
                        return "done";
                    }),
                },
            });

            list.Add(new Tweak
            {
                Id = "clean.recycle", Section = Section.Cleanup, Group = "Files", Level = Level.Heavy, OneShot = true,
                Title = "Empty the Recycle Bin",
                Summary = "Permanently deletes everything that's in the Recycle Bin.",
                Gain = "Free disk space.",
                TradeOff = "Files in the Recycle Bin can't be recovered afterwards. Have a look inside first.",
                Affects = A("disk"),
                Presets = Preset.None,
                Measure = e => SystemOps.RecycleBinSize(),
                Actions =
                {
                    new Job("Recycle Bin", "empty it on every drive", e =>
                    {
                        long size = SystemOps.RecycleBinSize();
                        e.Sys.EmptyRecycleBin();
                        return "freed " + Fmt.Bytes(Math.Max(0, size));
                    }),
                },
            });
        }

        // ===================================================================================
        // EXTRAS — quality of life
        // ===================================================================================
        static void Extras(List<Tweak> list)
        {
            const string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

            list.Add(new Tweak
            {
                Id = "qol.extensions", Section = Section.Extras, Group = "File Explorer & taskbar", Level = Level.Minor,
                Title = "Show file extensions",
                Summary = "Shows the end of file names, like .exe, .pdf and .zip, in File Explorer.",
                Gain = "You can tell a real document from a disguised program (\"invoice.pdf.exe\").",
                TradeOff = "None.",
                Affects = A("safety"),
                After = After.Explorer,
                Presets = All3,
                Actions = { Dw(CU, adv, "HideFileExt", 0, "1") },
            });

            list.Add(new Tweak
            {
                Id = "qol.endtask", Section = Section.Extras, Group = "File Explorer & taskbar", Level = Level.Minor,
                Title = "\"End task\" on taskbar buttons",
                Summary = "Adds End task to the right-click menu of taskbar buttons, so you can close a frozen game without opening Task Manager.",
                Gain = "A quick way out of a hung app.",
                TradeOff = "None.",
                Affects = A("desktop"),
                Presets = GS,
                Unavailable = e => e.Info.Build >= 22631 ? null : "Needs Windows 11 23H2 or newer", Requires = "Windows 11 23H2 or newer",
                Actions = { Dw(CU, adv + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, "not set") },
            });

            list.Add(new Tweak
            {
                Id = "qol.contextmenu", Section = Section.Extras, Group = "File Explorer & taskbar", Level = Level.Minor,
                Title = "Classic right-click menu",
                Summary = "Brings back the full right-click menu in Windows 11, without the extra \"Show more options\" click.",
                Gain = "All your context menu entries in one click.",
                TradeOff = "You lose the new compact menu's icons at the top.",
                Affects = A("desktop"),
                After = After.Explorer,
                Presets = Preset.None,
                Unavailable = Win11Only, Requires = "Windows 11",
                Actions = { Sz(CU, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "", "key not present") },
            });

            list.Add(new Tweak
            {
                Id = "qol.taskbar", Section = Section.Extras, Group = "File Explorer & taskbar", Level = Level.Minor,
                Title = "Tidier taskbar",
                Summary = "Shrinks the search box on the taskbar to an icon and hides the Task View button.",
                Gain = "More room on the taskbar for the apps you actually use. Search still works: press Win and type.",
                TradeOff = "Task View is still there with Win+Tab.",
                Affects = A("desktop"),
                After = After.Explorer,
                Presets = Preset.None,
                Actions =
                {
                    Dw(CU, @"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 1, "2 (search box)"),
                    Dw(CU, adv, "ShowTaskViewButton", 0, "1"),
                },
            });

            list.Add(new Tweak
            {
                Id = "sound.ducking", Section = Section.Extras, Group = "Sound & games", Level = Level.Minor,
                Title = "Keep game sound up during voice calls",
                Summary = "Stops Windows from turning other sounds down by 80% whenever it thinks you're in a call (Discord, Teams, the Xbox app).",
                Gain = "Your game, music and videos stay at the volume you set while you talk.",
                TradeOff = "None. You can still lower the volume of any app yourself.",
                Affects = A("sound"),
                Presets = All3,
                Actions = { Dw(CU, @"Software\Microsoft\Multimedia\Audio", "UserDuckingPreference", 3, "1 (reduce by 80%)") },
            });

            list.Add(new Tweak
            {
                Id = "games.xboxbutton", Section = Section.Extras, Group = "Sound & games", Level = Level.Minor,
                Title = "Xbox button doesn't open Game Bar",
                Summary = "Stops the Xbox button on a controller from opening the Game Bar overlay on top of your game.",
                Gain = "No overlay popping up when you press the Xbox button by accident or for Steam's own menu.",
                TradeOff = "Open Game Bar with Win+G instead.",
                Affects = A("games"),
                Presets = Preset.None,
                Actions = { Dw(CU, @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0, "1") },
            });

            list.Add(new Tweak
            {
                Id = "qol.wudrivers", Section = Section.Extras, Group = "Windows Update", Level = Level.Minor,
                Title = "Keep Windows Update away from your drivers",
                Summary = "Stops Windows Update from installing driver updates, so it can't swap out the graphics driver you installed yourself.",
                Gain = "Your graphics driver stays the version you chose.",
                TradeOff = "You update drivers yourself: through your graphics card's app and your PC or motherboard maker's website. Security updates for Windows are not affected.",
                Affects = A("drivers"),
                Presets = Preset.None,
                Actions = { Dw(LM, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", 1, "not set") },
            });

            list.Add(new Tweak
            {
                Id = "power.hibernate", Section = Section.Extras, Group = "Power", Level = Level.Heavy,
                Title = "Turn off hibernation",
                Summary = "Deletes the hibernation file (hiberfil.sys) and turns off Hibernate and Fast Startup.",
                Gain = "Frees disk space, usually around 40% of your memory size: about 6 to 13 GB on a PC with 16 to 32 GB. Every shutdown becomes a clean start.",
                TradeOff = "No Hibernate option, and a cold boot takes a few seconds longer. On laptops it's usually better to keep it.",
                Affects = A("disk"),
                Presets = Preset.None,
                Warning = e => e.Info.IsLaptop ? "This PC runs on a battery. Laptops use hibernation to save your work when the battery runs low." : null,
                Actions = { new HibernateOff() },
            });
        }
    }
}
