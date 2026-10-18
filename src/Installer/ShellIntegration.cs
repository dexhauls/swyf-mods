using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SwyfInstaller
{
    /// <summary>
    /// Makes the hub a small "installed" app for the current user (no admin): copies itself to
    /// %LOCALAPPDATA%\SWYF Mod Hub, opens .swyfmod files on double-click and has a Start menu shortcut.
    /// </summary>
    internal static class ShellIntegration
    {
        private const string ProgId = "SwyfModHub.Mod";

        internal static string InstalledExe =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SWYF Mod Hub", "SWYF-Hub.exe");

        internal static void Register()
        {
            var exe = InstalledExe;
            CopySelfTo(exe);

            using (var ext = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.swyfmod"))
                ext.SetValue("", ProgId);
            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                prog.SetValue("", Lang.T("Mod pro Scam With Your Friends", "Scam With Your Friends mod"));
                using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
                using (var cmd = prog.CreateSubKey(@"shell\open\command")) cmd.SetValue("", $"\"{exe}\" \"%1\"");
            }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED

            CreateShortcut(exe);
        }

        /// <summary>
        /// Run at every start: a newer hub (say, downloaded again from the release) replaces the installed copy, so the
        /// Start menu shortcut and double-clicked .swyfmod files open the new one. Only when the hub was installed
        /// before (the base installation does the first copy) and never with an older version.
        /// </summary>
        internal static void UpdateInstalledCopy()
        {
            var exe = InstalledExe;
            try
            {
                var leftover = exe + ".old";
                if (File.Exists(leftover)) File.Delete(leftover);
            }
            catch (Exception) { /* the old copy is still running; next time */ }
            if (!File.Exists(exe)) return;
            var current = Assembly.GetExecutingAssembly().Location;
            if (SamePath(current, exe)) return;
            if (VersionOf(current) <= VersionOf(exe)) return;
            CopySelfTo(exe);
        }

        private static void CopySelfTo(string exe)
        {
            var current = Assembly.GetExecutingAssembly().Location;
            if (SamePath(current, exe)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(exe));
            try
            {
                File.Copy(current, exe, true);
            }
            catch (IOException)
            {
                // The installed copy is running. Windows lets a running program's file be renamed, so move it aside
                // (deleted on a later start) and put the new one in its place.
                try
                {
                    var old = exe + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(exe, old);
                    File.Copy(current, exe, true);
                }
                catch (Exception) { /* keep the old one */ }
            }
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        private static Version VersionOf(string path)
        {
            try
            {
                var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart);
            }
            catch (Exception) { return new Version(0, 0); }
        }

        private static void CreateShortcut(string exe)
        {
            try
            {
                var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                var link = Path.Combine(programs, "SWYF Mod Hub.lnk");
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                var shell = Activator.CreateInstance(shellType);
                var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                var t = shortcut.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exe });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(exe) });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { Lang.T("Mody pro Scam With Your Friends", "Mods for Scam With Your Friends") });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                Marshal.ReleaseComObject(shortcut);
                Marshal.ReleaseComObject(shell);
            }
            catch
            {
                // A missing shortcut isn't worth failing the install over.
            }
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
    }
}
