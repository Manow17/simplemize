using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Simplemize
{
    public static class Security
    {
        static readonly SecurityIdentifier Admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        static readonly SecurityIdentifier Users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        static bool Trusted(IdentityReference owner) => owner != null && (owner.Equals(Admins) || owner.Equals(SystemSid));

        /// <summary>
        /// The backup in C:\ProgramData\Simplemize is replayed with administrator rights on undo, so only
        /// administrators may write there. Creates the folder locked down (Administrators and SYSTEM: full,
        /// Users: read), re-applies that to an existing folder, and moves aside any file in it that a
        /// non-administrator created. Returns an error message, or null when the folder is safe.
        /// </summary>
        public static string SecureDataDir(string dir, Log log)
        {
            try
            {
                var sec = new DirectorySecurity();
                sec.SetAccessRuleProtection(true, false);
                var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                sec.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
                sec.SetOwner(Admins);

                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir, sec);
                    return null;
                }

                var info = new DirectoryInfo(dir);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    return "The folder " + dir + " is a link to somewhere else. Delete it and start Simplemize again.";

                bool folderTrusted = Trusted(info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)));
                info.SetAccessControl(sec);

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                foreach (var f in info.GetFiles())
                {
                    if (f.Name.Contains(".untrusted-")) continue;
                    var owner = f.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
                    bool isRecord = f.Name.StartsWith("backup.json", StringComparison.OrdinalIgnoreCase);
                    if (Trusted(owner) && (folderTrusted || !isRecord)) continue;
                    var aside = f.FullName + ".untrusted-" + stamp;
                    f.MoveTo(aside);
                    log.Error("moved aside " + f.Name + ": it was not created by an administrator, so it can't be trusted for undo (" + aside + ")");
                }
                return null;
            }
            catch (Exception ex)
            {
                return "Couldn't secure " + dir + " (" + ex.Message + ").";
            }
        }

        public static string CurrentUserSid()
        {
            using (var id = WindowsIdentity.GetCurrent()) return id.User?.Value;
        }

        /// <summary>
        /// True when the signed-in desktop (Explorer) belongs to a different account than this process,
        /// e.g. a standard user typed an administrator's password at the UAC prompt.
        /// </summary>
        public static bool ShellRunsAsAnotherUser()
        {
            try
            {
                var me = CurrentUserSid();
                int session = Process.GetCurrentProcess().SessionId;
                using (var s = new ManagementObjectSearcher("SELECT Handle, SessionId FROM Win32_Process WHERE Name = 'explorer.exe'"))
                    foreach (ManagementObject o in s.Get())
                        using (o)
                        {
                            if (Convert.ToInt32(o["SessionId"]) != session) continue;
                            using (var outp = o.InvokeMethod("GetOwnerSid", null, null))
                            {
                                var sid = outp?["Sid"] as string;
                                if (!string.IsNullOrEmpty(sid)) return !string.Equals(sid, me, StringComparison.OrdinalIgnoreCase);
                            }
                        }
            }
            catch { }
            return false;
        }
    }
}
