using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Simplemize
{
    // =========================================================================================
    // NVIDIA driver settings (the same database NVIDIA Control Panel and Profile Inspector edit).
    // IDs and the NVDRS_SETTING layout come from NVIDIA's public NVAPI headers (github.com/NVIDIA/nvapi).
    // =========================================================================================
    public static class NvDrs
    {
        // function ids (nvapi_interface.h)
        const uint ID_Initialize = 0x0150e828;
        const uint ID_CreateSession = 0x0694d52e;
        const uint ID_DestroySession = 0xdad9cff8;
        const uint ID_LoadSettings = 0x375dbd6b;
        const uint ID_SaveSettings = 0xfcbc7e14;
        const uint ID_GetCurrentGlobalProfile = 0x617bff9f;
        const uint ID_GetSetting = 0x73bf8338;
        const uint ID_SetSetting = 0x577dd202;
        const uint ID_DeleteProfileSetting = 0xe4a26362;
        const uint ID_GetSettingNameFromId = 0xd61cbe6e;

        public const int OK = 0, SETTING_NOT_FOUND = -160;

        // NVDRS_SETTING_V1 (pack 4): version, name[2048 wchar], id, type, location, isCurrentPredefined,
        // isPredefinedValid, predefined union (4100 bytes), current union (4100 bytes) = 12320 bytes.
        const int SettingSize = 12320;
        const uint SettingVersion = SettingSize | (1u << 16);
        const int OffId = 4100, OffType = 4104, OffLocation = 4108, OffIsPredefined = 4112, OffPredefined = 4120, OffCurrent = 8220;

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CreateSessionFn(out IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SessionFn(IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ProfileFn(IntPtr session, out IntPtr profile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GetSettingFn(IntPtr session, IntPtr profile, uint id, [In, Out] byte[] setting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetSettingFn(IntPtr session, IntPtr profile, [In, Out] byte[] setting);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DeleteSettingFn(IntPtr session, IntPtr profile, uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NameFn(uint id, [In, Out] byte[] name);

        static T Fn<T>(uint id) where T : class
        {
            var p = QueryInterface(id);
            if (p == IntPtr.Zero) throw new InvalidOperationException("This NVIDIA driver doesn't offer function 0x" + id.ToString("X8") + ".");
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        static readonly object gate = new object();
        static bool? available;

        /// <summary>True when an NVIDIA driver with NVAPI is installed and initialises.</summary>
        public static bool Available
        {
            get
            {
                lock (gate)
                {
                    if (available.HasValue) return available.Value;
                    available = false;
                    try
                    {
                        if (!Environment.Is64BitProcess) return false;
                        if (!File.Exists(Path.Combine(Environment.SystemDirectory, "nvapi64.dll"))) return false;
                        available = Fn<InitFn>(ID_Initialize)() == OK;
                    }
                    catch { available = false; }
                    return available.Value;
                }
            }
        }

        public sealed class Value
        {
            public int Status;          // OK or SETTING_NOT_FOUND
            public uint Current;
            public bool UserSet;        // set in the global profile by the user (not a predefined/default value)
            public int Location;
            public string Name;
        }

        /// <summary>Runs <paramref name="body"/> with an open session on the current global profile.</summary>
        static T WithGlobalProfile<T>(Func<IntPtr, IntPtr, T> body)
        {
            if (!Available) throw new InvalidOperationException("No NVIDIA driver found.");
            lock (gate)
            {
                IntPtr session;
                int st = Fn<CreateSessionFn>(ID_CreateSession)(out session);
                if (st != OK) throw new InvalidOperationException("NVIDIA driver: couldn't open settings (" + st + ").");
                try
                {
                    st = Fn<SessionFn>(ID_LoadSettings)(session);
                    if (st != OK) throw new InvalidOperationException("NVIDIA driver: couldn't load settings (" + st + ").");
                    st = Fn<ProfileFn>(ID_GetCurrentGlobalProfile)(session, out var profile);
                    if (st != OK) throw new InvalidOperationException("NVIDIA driver: couldn't find the global profile (" + st + ").");
                    return body(session, profile);
                }
                finally { Fn<SessionFn>(ID_DestroySession)(session); }
            }
        }

        public static Value Read(uint settingId) => WithGlobalProfile((session, profile) =>
        {
            var buf = new byte[SettingSize];
            BitConverter.GetBytes(SettingVersion).CopyTo(buf, 0);
            int st = Fn<GetSettingFn>(ID_GetSetting)(session, profile, settingId, buf);
            var v = new Value { Status = st };
            if (st == OK)
            {
                v.Current = BitConverter.ToUInt32(buf, OffCurrent);
                v.Location = BitConverter.ToInt32(buf, OffLocation);
                bool predefined = BitConverter.ToUInt32(buf, OffIsPredefined) != 0;
                v.UserSet = v.Location == 0 && !predefined;     // 0 = NVDRS_CURRENT_PROFILE_LOCATION
                v.Name = Encoding.Unicode.GetString(buf, 4, 4096).Split('\0')[0];
            }
            else if (st != SETTING_NOT_FOUND) throw new InvalidOperationException("NVIDIA driver: couldn't read setting 0x" + settingId.ToString("X8") + " (" + st + ").");
            return v;
        });

        public static void Write(uint settingId, uint value) => WithGlobalProfile((session, profile) =>
        {
            var buf = new byte[SettingSize];
            BitConverter.GetBytes(SettingVersion).CopyTo(buf, 0);
            BitConverter.GetBytes(settingId).CopyTo(buf, OffId);
            BitConverter.GetBytes(0).CopyTo(buf, OffType);         // NVDRS_DWORD_TYPE
            BitConverter.GetBytes(value).CopyTo(buf, OffCurrent);
            int st = Fn<SetSettingFn>(ID_SetSetting)(session, profile, buf);
            if (st != OK) throw new InvalidOperationException("NVIDIA driver refused the change (" + st + ").");
            st = Fn<SessionFn>(ID_SaveSettings)(session);
            if (st != OK) throw new InvalidOperationException("NVIDIA driver couldn't save settings (" + st + ").");
            return 0;
        });

        /// <summary>Removes the user value so the driver default applies again.</summary>
        public static void Reset(uint settingId) => WithGlobalProfile((session, profile) =>
        {
            int st = Fn<DeleteSettingFn>(ID_DeleteProfileSetting)(session, profile, settingId);
            if (st != OK && st != SETTING_NOT_FOUND) throw new InvalidOperationException("NVIDIA driver refused to reset the setting (" + st + ").");
            st = Fn<SessionFn>(ID_SaveSettings)(session);
            if (st != OK) throw new InvalidOperationException("NVIDIA driver couldn't save settings (" + st + ").");
            return 0;
        });

        public static string NameOf(uint settingId)
        {
            if (!Available) return null;
            lock (gate)
            {
                var buf = new byte[4096];
                return Fn<NameFn>(ID_GetSettingNameFromId)(settingId, buf) == OK ? Encoding.Unicode.GetString(buf).Split('\0')[0] : null;
            }
        }
    }

    // =========================================================================================
    // Power plan settings (CPU boost, core parking, USB and PCIe power saving). Plugged-in (AC) values only.
    // =========================================================================================
    public static class PowerSettings
    {
        [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

        public static Guid? ActiveScheme()
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0 || p == IntPtr.Zero) return null;
            try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
            finally { LocalFree(p); }
        }

        /// <summary>The plugged-in value, or null if this PC doesn't have the setting.</summary>
        public static uint? ReadAC(Guid scheme, Guid sub, Guid setting)
        {
            return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var v) == 0 ? v : (uint?)null;
        }

        public static void WriteAC(Guid scheme, Guid sub, Guid setting, uint value)
        {
            uint rc = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
            if (rc != 0) throw new InvalidOperationException("Windows refused the power setting (error " + rc + ").");
            var active = ActiveScheme();
            if (active == scheme) PowerSetActiveScheme(IntPtr.Zero, ref scheme);   // re-apply so it takes effect now
        }
    }

    // =========================================================================================
    // Displays: current mode and the fastest refresh rate the driver offers at the same resolution.
    // =========================================================================================
    public sealed class DisplayInfo
    {
        public string Device;        // \\.\DISPLAY1
        public string Name;          // monitor name if Windows knows it
        public int Width, Height, Bits, Hz, BestHz;
        public bool Primary;
    }

    public static class Displays
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr lParam);

        const int ENUM_CURRENT_SETTINGS = -1;
        const int ATTACHED = 0x1, PRIMARY = 0x4, DM_INTERLACED = 0x2, DM_DISPLAYFREQUENCY = 0x400000;
        const uint CDS_UPDATEREGISTRY = 0x1, CDS_TEST = 0x2;

        static DEVMODE NewMode() => new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };

        public static List<DisplayInfo> List()
        {
            var list = new List<DisplayInfo>();
            for (uint i = 0; i < 16; i++)
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & ATTACHED) == 0) continue;
                var cur = NewMode();
                if (!EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref cur)) continue;

                int best = cur.dmDisplayFrequency;
                var m = NewMode();
                for (int n = 0; EnumDisplaySettings(dd.DeviceName, n, ref m); n++)
                {
                    if (m.dmPelsWidth == cur.dmPelsWidth && m.dmPelsHeight == cur.dmPelsHeight && m.dmBitsPerPel == cur.dmBitsPerPel
                        && (m.dmDisplayFlags & DM_INTERLACED) == 0 && m.dmDisplayFrequency > best && m.dmDisplayFrequency <= 1000)
                        best = m.dmDisplayFrequency;
                    m = NewMode();
                }

                var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                string name = EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0) ? mon.DeviceString : null;
                list.Add(new DisplayInfo
                {
                    Device = dd.DeviceName,
                    Name = string.IsNullOrWhiteSpace(name) ? dd.DeviceName.Replace(@"\\.\", "") : name,
                    Width = cur.dmPelsWidth,
                    Height = cur.dmPelsHeight,
                    Bits = cur.dmBitsPerPel,
                    Hz = cur.dmDisplayFrequency,
                    BestHz = best,
                    Primary = (dd.StateFlags & PRIMARY) != 0,
                });
            }
            return list;
        }

        /// <summary>Sets the refresh rate (same resolution). Returns 0 on success, a DISP_CHANGE code otherwise.</summary>
        public static int SetRefresh(string device, int hz, bool testOnly)
        {
            var cur = NewMode();
            if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref cur)) return -100;
            cur.dmFields = DM_DISPLAYFREQUENCY;
            cur.dmDisplayFrequency = hz;
            int rc = ChangeDisplaySettingsEx(device, ref cur, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
            if (rc != 0 || testOnly) return rc;
            return ChangeDisplaySettingsEx(device, ref cur, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        }
    }
}
