using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SwyfInstaller
{
    // ---------- mod.json (see ModSource/MOD-FORMAT.md) ----------

    [DataContract]
    public class ModManifest
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "version")] public string Version { get; set; }
        [DataMember(Name = "author")] public string Author { get; set; }
        [DataMember(Name = "description")] public string Description { get; set; }
        [DataMember(Name = "icon")] public string Icon { get; set; }
        [DataMember(Name = "tags")] public List<string> Tags { get; set; }
        [DataMember(Name = "dll")] public string Dll { get; set; }
        [DataMember(Name = "downloads")] public List<ModDownload> Downloads { get; set; }
        [DataMember(Name = "settings")] public List<ModSetting> Settings { get; set; }

        // Optional English texts; the hub shows them when the game is not in Czech/Slovak.
        [DataMember(Name = "name_en")] public string NameEn { get; set; }
        [DataMember(Name = "description_en")] public string DescriptionEn { get; set; }
        [DataMember(Name = "tags_en")] public List<string> TagsEn { get; set; }

        public string LocalName => Lang.Pick(Name, NameEn);
        public string LocalDescription => Lang.Pick(Description, DescriptionEn);
        public List<string> LocalTags => Lang.Pick(Tags, TagsEn);
    }

    [DataContract]
    public class ModDownload
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "description")] public string Description { get; set; }
        [DataMember(Name = "url")] public string Url { get; set; }
        [DataMember(Name = "sha256")] public string Sha256 { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
        [DataMember(Name = "path")] public string Path { get; set; }
        [DataMember(Name = "default")] public bool Default { get; set; }
        [DataMember(Name = "name_en")] public string NameEn { get; set; }
        [DataMember(Name = "description_en")] public string DescriptionEn { get; set; }

        public string LocalName => Lang.Pick(Name, NameEn);
        public string LocalDescription => Lang.Pick(Description, DescriptionEn);
    }

    /// <summary>A choice shown on the mod's card, stored as one key of a BepInEx config file.</summary>
    [DataContract]
    public class ModSetting
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "name_en")] public string NameEn { get; set; }
        [DataMember(Name = "file")] public string File { get; set; }        // relative to the game folder
        [DataMember(Name = "section")] public string Section { get; set; }
        [DataMember(Name = "key")] public string Key { get; set; }
        [DataMember(Name = "default")] public string Default { get; set; }
        [DataMember(Name = "options")] public List<ModSettingOption> Options { get; set; }

        public string LocalName => Lang.Pick(Name, NameEn);
    }

    [DataContract]
    public class ModSettingOption
    {
        [DataMember(Name = "value")] public string Value { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "name_en")] public string NameEn { get; set; }
        [DataMember(Name = "hint")] public string Hint { get; set; }
        [DataMember(Name = "hint_en")] public string HintEn { get; set; }
        [DataMember(Name = "clears")] public List<string> Clears { get; set; }  // files in Mods/<id> deleted when picked

        public string LocalName => Lang.Pick(Name, NameEn);
        public string LocalHint => Lang.Pick(Hint, HintEn);
    }

    [DataContract]
    internal class HubState
    {
        [DataMember(Name = "baseVersion")] public string BaseVersion { get; set; }
    }

    [DataContract]
    internal class ManagerStatus
    {
        [DataMember(Name = "manager")] public string Manager { get; set; }
        [DataMember(Name = "loaded")] public List<LoadedEntry> Loaded { get; set; }
        [DataMember(Name = "errors")] public List<string> Errors { get; set; }
    }

    [DataContract]
    internal class LoadedEntry
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "version")] public string Version { get; set; }
    }

    /// <summary>A mod found in &lt;game&gt;\Mods.</summary>
    public class InstalledMod
    {
        public string Id { get; set; }
        public ModManifest Manifest { get; set; }
        public string Folder { get; set; }   // null for a loose DLL
        public string LooseDll { get; set; }
        public bool Enabled { get; set; }
        public long SizeOnDisk { get; set; }
    }

    /// <summary>Download progress: bytes so far / total for the current file.</summary>
    public struct TransferProgress
    {
        public string Label;
        public long Done;
        public long Total;
        public double BytesPerSecond;
    }

    /// <summary>
    /// The hub's file operations: installing the base (mod loader, stripped-game fix, Mod Manager) from the
    /// embedded payload.zip, and installing / toggling / removing .swyfmod packages in &lt;game&gt;\Mods.
    /// While the game runs, the Mod Manager picks those changes up live.
    /// </summary>
    public sealed class Hub
    {
        internal const string StageLoader = "loader";
        internal const string StageFix = "fix";
        internal const string StageManager = "manager";

        private const string StateFile = @"BepInEx\swyf-hub.json";
        private const string StatusFile = @"BepInEx\swyf-modmanager-status.json";
        private const string ManagerDll = @"BepInEx\plugins\SwyfModManager\SwyfModManager.dll";
        private const string BepInExConfig = @"BepInEx\config\BepInEx.cfg";
        private static readonly string[] LoaderFiles = { "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" };
        private static readonly Regex IdPattern = new Regex("^[A-Za-z0-9_-]+$");

        private readonly byte[] _payload;

        public Hub()
        {
            using (var s = typeof(Hub).Assembly.GetManifestResourceStream("payload.zip"))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                _payload = ms.ToArray();
            }
        }

        public static string Version => typeof(Hub).Assembly.GetName().Version.ToString(3);

        // ---------- Base ----------

        /// <summary>Some BepInEx/doorstop setup is present (ours or a manual one).</summary>
        public static bool HasAnyLoader(string gameDir) => File.Exists(Path.Combine(gameDir, "winhttp.dll"));

        /// <summary>The base was installed by this hub (it wrote its state file).</summary>
        public static bool IsManagedByHub(string gameDir) => File.Exists(Path.Combine(gameDir, StateFile));

        public static bool IsBaseInstalled(string gameDir) =>
            File.Exists(Path.Combine(gameDir, "winhttp.dll")) && File.Exists(Path.Combine(gameDir, ManagerDll));

        /// <summary>True when the base is missing or older than the one in this hub.</summary>
        public static bool BaseNeedsInstall(string gameDir)
        {
            if (!IsBaseInstalled(gameDir)) return true;
            var state = ReadJson<HubState>(Path.Combine(gameDir, StateFile));
            return state?.BaseVersion == null || new System.Version(state.BaseVersion) < new System.Version(Version);
        }

        public void InstallBase(string gameDir, IProgress<double> progress, IProgress<string> stage)
        {
            using (var zip = new ZipArchive(new MemoryStream(_payload, false), ZipArchiveMode.Read))
            {
                string StageOf(ZipArchiveEntry e)
                {
                    var p = Norm(e.FullName);
                    if (p.StartsWith("base/BepInEx/plugins/")) return StageManager;
                    if (p.StartsWith("base/BepInEx/unstripped_corlib/") || p.StartsWith("base/BepInEx/patchers/")) return StageFix;
                    return StageLoader;
                }
                var order = new[] { StageLoader, StageFix, StageManager };
                var entries = zip.Entries.Where(e => Norm(e.FullName).StartsWith("base/") && e.Name.Length > 0)
                    .OrderBy(e => Array.IndexOf(order, StageOf(e))).ToList();
                string current = null;
                for (var i = 0; i < entries.Count; i++)
                {
                    var st = StageOf(entries[i]);
                    if (st != current) stage.Report(current = st);
                    Extract(entries[i], Path.Combine(gameDir, Norm(entries[i].FullName).Substring("base/".Length)));
                    progress.Report((i + 1) / (double)entries.Count);
                }
            }
            Directory.CreateDirectory(Path.Combine(gameDir, "Mods"));
            DisableBepInExConsole(gameDir);
            WriteJson(Path.Combine(gameDir, StateFile), new HubState { BaseVersion = Version });
        }

        /// <summary>
        /// Sets [Logging.Console] Enabled = false in BepInEx.cfg, leaving every other setting as it is. The console
        /// window shares its input queue with the game's main thread, so each mouse move waits on conhost and
        /// turning the camera costs 3–5 ms per frame (measured with the Výkon mod). The log still goes to
        /// BepInEx\LogOutput.log.
        /// </summary>
        private static void DisableBepInExConsole(string gameDir)
        {
            var path = Path.Combine(gameDir, BepInExConfig);
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            var section = lines.FindIndex(l => l.Trim().Equals("[Logging.Console]", StringComparison.OrdinalIgnoreCase));
            if (section < 0)
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                lines.AddRange(new[] { "[Logging.Console]", "", "## Enables showing a console for log output.",
                    "# Setting type: Boolean", "# Default value: false", "Enabled = false", "" });
            }
            else
            {
                var end = lines.FindIndex(section + 1, l => l.TrimStart().StartsWith("["));
                if (end < 0) end = lines.Count;
                var key = lines.FindIndex(section + 1, end - section - 1, l => Regex.IsMatch(l, @"^\s*Enabled\s*="));
                if (key >= 0) lines[key] = "Enabled = false";
                else lines.Insert(section + 1, "Enabled = false");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
        }

        public static void UninstallAll(string gameDir)
        {
            foreach (var f in LoaderFiles) DeleteFile(Path.Combine(gameDir, f));
            foreach (var log in Directory.GetFiles(gameDir, "preloader_*.log")) File.Delete(log);
            DeleteDir(Path.Combine(gameDir, "BepInEx"));
            DeleteDir(Path.Combine(gameDir, "Mods"));
        }

        // ---------- Mods ----------

        public static List<InstalledMod> ListMods(string gameDir)
        {
            var mods = new List<InstalledMod>();
            var modsDir = Path.Combine(gameDir, "Mods");
            if (!Directory.Exists(modsDir)) return mods;
            foreach (var dir in Directory.GetDirectories(modsDir))
            {
                var manifest = ReadJson<ModManifest>(Path.Combine(dir, "mod.json"));
                if (manifest == null) continue;
                mods.Add(new InstalledMod
                {
                    Id = manifest.Id ?? Path.GetFileName(dir),
                    Manifest = manifest,
                    Folder = dir,
                    Enabled = !File.Exists(Path.Combine(dir, "disabled")),
                    SizeOnDisk = SafeSize(dir)
                });
            }
            foreach (var dll in Directory.GetFiles(modsDir, "*.dll"))
            {
                var id = Path.GetFileNameWithoutExtension(dll);
                if (mods.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                mods.Add(new InstalledMod
                {
                    Id = id,
                    Manifest = new ModManifest { Id = id, Name = id, Description = "Ručně přidaná DLL ve složce Mods.", DescriptionEn = "DLL added by hand to the Mods folder." },
                    LooseDll = dll,
                    Enabled = true,
                    SizeOnDisk = new FileInfo(dll).Length
                });
            }
            return mods.OrderBy(m => m.Manifest.LocalName ?? m.Id, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>
        /// Downloads from the guide page come as .zip (the page can't offer .swyfmod). A .zip with mod.json at
        /// its root is a package as it is; otherwise the first .swyfmod inside is extracted to a temp file.
        /// Returns the path to install from (unchanged for .swyfmod / .dll).
        /// </summary>
        public static string ResolvePackagePath(string path)
        {
            if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return path;
            using (var zip = ZipFile.OpenRead(path))
            {
                if (zip.Entries.Any(e => Norm(e.FullName) == "mod.json")) return path;
                var inner = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".swyfmod", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException(Lang.T("V zipu není žádný mod (.swyfmod).", "There is no mod (.swyfmod) in this zip."));
                var temp = Path.Combine(Path.GetTempPath(), "swyf-hub", Guid.NewGuid().ToString("N"), inner.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(temp));
                inner.ExtractToFile(temp, true);
                return temp;
            }
        }

        /// <summary>Reads mod.json from a .swyfmod, or makes one up for a plain .dll.</summary>
        public static ModManifest ReadPackage(string path)
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var id = Regex.Replace(Path.GetFileNameWithoutExtension(path), "[^A-Za-z0-9_-]", "_");
                return new ModManifest { Id = id, Name = Path.GetFileNameWithoutExtension(path), Dll = Path.GetFileName(path),
                    Description = "Mod přidaný jako samotná DLL.", DescriptionEn = "Mod added as a plain DLL.", Version = FileVersionInfo.GetVersionInfo(path).FileVersion };
            }
            using (var zip = ZipFile.OpenRead(path))
            {
                var entry = zip.Entries.FirstOrDefault(e => Norm(e.FullName) == "mod.json")
                            ?? throw new InvalidDataException(Lang.T("V balíčku chybí mod.json, tohle asi není mod pro SWYF.",
                                "The package has no mod.json, so it's probably not a SWYF mod."));
                ModManifest m;
                using (var s = entry.Open()) m = ReadJson<ModManifest>(s);
                if (m?.Id == null || !IdPattern.IsMatch(m.Id)) throw new InvalidDataException(Lang.T("mod.json má neplatné id.", "mod.json has an invalid id."));
                if (string.IsNullOrEmpty(m.Dll) || !zip.Entries.Any(e => Norm(e.FullName).Equals("files/" + m.Dll, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException(Lang.T($"V balíčku chybí {m.Dll ?? "DLL modu"}.", $"The package is missing {m.Dll ?? "the mod's DLL"}."));
                return m;
            }
        }

        /// <summary>
        /// Installs a package into Mods\&lt;id&gt;: files first, mod.json last (that's what the Mod Manager
        /// reacts to), then the selected downloads. Existing downloaded files are kept on update.
        /// </summary>
        public static async Task InstallPackageAsync(string gameDir, string path, ModManifest m, ICollection<string> downloadIds,
            IProgress<TransferProgress> progress)
        {
            var dir = Path.Combine(gameDir, "Mods", m.Id);
            Directory.CreateDirectory(dir);

            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(path, Path.Combine(dir, m.Dll), true);
            }
            else
            {
                using (var zip = ZipFile.OpenRead(path))
                {
                    var files = zip.Entries.Where(e => Norm(e.FullName).StartsWith("files/") && e.Name.Length > 0).ToList();
                    for (var i = 0; i < files.Count; i++)
                    {
                        var rel = Norm(files[i].FullName).Substring("files/".Length);
                        var target = Path.GetFullPath(Path.Combine(dir, rel));
                        if (!target.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) continue; // no ../ tricks
                        Extract(files[i], target);
                        progress.Report(new TransferProgress { Label = Lang.T("Rozbaluju soubory", "Extracting files"), Done = i + 1, Total = files.Count });
                    }
                }
            }

            foreach (var d in (m.Downloads ?? new List<ModDownload>()).Where(d => downloadIds.Contains(d.Id)))
                await DownloadAsync(d, Path.Combine(dir, d.Path.Replace('/', '\\')), progress);

            DeleteFile(Path.Combine(dir, "disabled"));
            WriteJson(Path.Combine(dir, "mod.json"), m);
        }

        public static void SetEnabled(InstalledMod mod, bool enabled)
        {
            if (mod.Folder == null) return;
            var marker = Path.Combine(mod.Folder, "disabled");
            if (enabled) DeleteFile(marker);
            else File.WriteAllText(marker, Lang.T("Mod je vypnutý v SWYF Mod Hubu.", "This mod is turned off in SWYF Mod Hub."));
        }

        // ---------- Mod settings (mod.json "settings" → a key in a BepInEx .cfg) ----------

        /// <summary>The setting's current value from its config file, or its default when the file or key is missing.</summary>
        public static string ReadSetting(string gameDir, ModSetting s)
        {
            try
            {
                var path = Path.Combine(gameDir, s.File.Replace('/', '\\'));
                if (File.Exists(path))
                {
                    string section = null;
                    foreach (var raw in File.ReadAllLines(path))
                    {
                        var line = raw.Trim();
                        if (line.StartsWith("[") && line.EndsWith("]")) section = line.Substring(1, line.Length - 2).Trim();
                        else if (section == s.Section && !line.StartsWith("#") && line.Contains("="))
                        {
                            var eq = line.IndexOf('=');
                            if (line.Substring(0, eq).Trim() == s.Key) return line.Substring(eq + 1).Trim();
                        }
                    }
                }
            }
            catch (IOException) { }
            return s.Default ?? "";
        }

        /// <summary>
        /// Writes the setting into its BepInEx config (creating the file, section or key if needed; everything else in the
        /// file stays) and deletes the files the option clears. Mods read their config at game start.
        /// </summary>
        public static void WriteSetting(string gameDir, InstalledMod mod, ModSetting s, ModSettingOption option)
        {
            var path = Path.Combine(gameDir, s.File.Replace('/', '\\'));
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            string section = null;
            int sectionEnd = -1, keyLine = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    if (section == s.Section) sectionEnd = i + 1;
                    continue;
                }
                if (section != s.Section) continue;
                sectionEnd = i + 1;
                var eq = line.IndexOf('=');
                if (!line.StartsWith("#") && eq > 0 && line.Substring(0, eq).Trim() == s.Key) keyLine = i;
            }
            var entry = $"{s.Key} = {option.Value}";
            if (keyLine >= 0) lines[keyLine] = entry;
            else if (sectionEnd >= 0) lines.Insert(sectionEnd, entry);
            else
            {
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                lines.Add($"[{s.Section}]");
                lines.Add("");
                lines.Add(entry);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, lines, new UTF8Encoding(false));

            var folder = mod.Folder ?? Path.Combine(gameDir, "Mods", mod.Id);
            foreach (var name in option.Clears ?? new List<string>())
            {
                if (name.Contains("..")) continue;
                DeleteFile(Path.Combine(folder, name.Replace('/', '\\')));
            }
        }

        public static void Remove(InstalledMod mod)
        {
            if (mod.Folder != null) DeleteDir(mod.Folder);
            else if (mod.LooseDll != null)
            {
                DeleteFile(mod.LooseDll);
                DeleteFile(Path.ChangeExtension(mod.LooseDll, ".pdb"));
            }
        }

        public static bool IsDownloaded(string gameDir, ModManifest m, ModDownload d)
        {
            var file = new FileInfo(Path.Combine(gameDir, "Mods", m.Id, d.Path.Replace('/', '\\')));
            return file.Exists && (d.Size <= 0 || file.Length == d.Size);
        }

        // ---------- Game / manager status ----------

        /// <summary>True if the game is running from <paramref name="gameDir"/>.</summary>
        public static bool IsGameRunning(string gameDir)
        {
            var target = Path.GetFullPath(gameDir).TrimEnd('\\') + "\\";
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(SteamLocator.ExeName)))
            {
                try
                {
                    if (p.MainModule.FileName.StartsWith(target, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch
                {
                    return true;
                }
            }
            return false;
        }

        internal static ManagerStatus ReadManagerStatus(string gameDir) => ReadJson<ManagerStatus>(Path.Combine(gameDir, StatusFile));

        // ---------- Downloads ----------

        private static async Task DownloadAsync(ModDownload d, string target, IProgress<TransferProgress> progress)
        {
            if (File.Exists(target) && (d.Size <= 0 || new FileInfo(target).Length == d.Size)) return;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var part = target + ".part";

            using (var http = new HttpClient { Timeout = TimeSpan.FromHours(6) })
            using (var response = await http.GetAsync(d.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? d.Size;
                using (var source = await response.Content.ReadAsStreamAsync())
                using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
                using (var sha = SHA256.Create())
                {
                    var buffer = new byte[1 << 20];
                    long done = 0;
                    var clock = Stopwatch.StartNew();
                    var lastReport = 0L;
                    int read;
                    while ((read = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await file.WriteAsync(buffer, 0, read);
                        sha.TransformBlock(buffer, 0, read, null, 0);
                        done += read;
                        if (clock.ElapsedMilliseconds - lastReport > 150 || done == total)
                        {
                            lastReport = clock.ElapsedMilliseconds;
                            progress.Report(new TransferProgress { Label = Lang.T($"Stahuju {d.LocalName}", $"Downloading {d.LocalName}"), Done = done, Total = total,
                                BytesPerSecond = done / Math.Max(0.001, clock.Elapsed.TotalSeconds) });
                        }
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    var hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                    if (!string.IsNullOrEmpty(d.Sha256) && !hash.Equals(d.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        file.Close();
                        File.Delete(part);
                        throw new InvalidDataException(Lang.T($"Stažený soubor {d.LocalName} je poškozený (nesedí kontrolní součet). Zkus to znovu.",
                            $"The downloaded file {d.LocalName} is corrupted (checksum mismatch). Please try again."));
                    }
                }
            }
            DeleteFile(target);
            File.Move(part, target);
        }

        // ---------- Helpers ----------

        private static string Norm(string zipPath) => zipPath.Replace('\\', '/');

        private static void Extract(ZipArchiveEntry entry, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            entry.ExtractToFile(target, true);
        }

        private static void DeleteFile(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private static void DeleteDir(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }

        private static long SafeSize(string dir)
        {
            try
            {
                return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            }
            catch
            {
                return 0;
            }
        }

        internal static T ReadJson<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    return ReadJson<T>(s);
            }
            catch
            {
                return null;
            }
        }

        private static T ReadJson<T>(Stream s) where T : class
        {
            using (var reader = new StreamReader(s, Encoding.UTF8))
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(reader.ReadToEnd())))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(ms);
        }

        private static void WriteJson<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(ms, value);
                File.WriteAllBytes(path, ms.ToArray());
            }
        }
    }
}
