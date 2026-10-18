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
            var current = Assembly.GetExecutingAssembly().Location;
            var exe = InstalledExe;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(exe));
                if (!string.Equals(Path.GetFullPath(current), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
                    File.Copy(current, exe, true);
            }
            catch (IOException)
            {
                // The installed copy is running; keep the old one.
            }

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
