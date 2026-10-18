using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SwyfModManager
{
    /// <summary>
    /// Loads every mod in &lt;game&gt;\Mods at startup and keeps watching the folder: mods added, updated,
    /// disabled or removed while the game runs are loaded / reloaded / unloaded on the fly.
    /// DLLs are read into memory (Assembly.Load(byte[])), so files never stay locked.
    /// See ModSource/MOD-FORMAT.md.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class ModManagerPlugin : BaseUnityPlugin
    {
        public const string Guid = "dex.swyf.modmanager";
        public const string Name = "SWYF Mod Manager";
        public const string Version = "1.0.3";

        private const double DebounceSeconds = 0.8;

        internal static ManualLogSource Log;

        private readonly Dictionary<string, LoadedMod> _loaded = new Dictionary<string, LoadedMod>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private string _modsDir;
        private FileSystemWatcher _watcher;
        private volatile bool _dirty;
        private double _dirtyAt;

        private void Awake()
        {
            Log = Logger;
            _modsDir = Path.Combine(Paths.GameRootPath, "Mods");
            Directory.CreateDirectory(_modsDir);

            Rescan(startup: true);

            _watcher = new FileSystemWatcher(_modsDir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler changed = (s, e) => MarkDirty();
            _watcher.Created += changed;
            _watcher.Changed += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (s, e) => MarkDirty();
            _watcher.EnableRaisingEvents = true;
            Log.LogInfo($"Watching {_modsDir} for mods.");
        }

        private void MarkDirty()
        {
            _dirtyAt = _clock.Elapsed.TotalSeconds; // file events come from a worker thread
            _dirty = true;
        }

        private void Update()
        {
            if (_dirty && _clock.Elapsed.TotalSeconds - _dirtyAt > DebounceSeconds)
            {
                _dirty = false;
                Rescan(startup: false);
            }
        }

        private void OnGUI() => Toasts.Draw();

        private void OnDestroy()
        {
            _watcher?.Dispose();
        }

        // ---------- Scanning ----------

        private void Rescan(bool startup)
        {
            List<ModEntry> wanted;
            try
            {
                wanted = Discover();
            }
            catch (Exception e)
            {
                Log.LogError($"Could not read the Mods folder: {e}");
                return;
            }

            // Unload mods that were removed or disabled.
            foreach (var id in _loaded.Keys.ToList())
            {
                if (wanted.Any(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
                var entryGone = _loaded[id].Entry;
                Unload(id);
                _errors.Remove(id);
                Toasts.Show(Lang.T($"Vypnuto: {entryGone.Name}", $"Turned off: {entryGone.DisplayNameEn}"), Toasts.Kind.Info);
            }

            var loadedNow = 0;
            foreach (var entry in wanted)
            {
                if (_loaded.TryGetValue(entry.Id, out var current))
                {
                    if (current.Entry.Stamp == entry.Stamp) continue;
                    Unload(entry.Id);
                    if (Load(entry))
                        Toasts.Show(Lang.T($"Aktualizováno: {entry.Name}{VersionSuffix(entry)}", $"Updated: {entry.DisplayNameEn}{VersionSuffix(entry)}"), Toasts.Kind.Ok);
                    continue;
                }
                // Don't retry a broken mod until its files change.
                if (_errors.TryGetValue(entry.Id, out var failedStamp) && failedStamp == entry.Stamp) continue;
                if (Load(entry))
                {
                    loadedNow++;
                    if (!startup)
                        Toasts.Show(Lang.T($"Načteno: {entry.Name}{VersionSuffix(entry)}", $"Loaded: {entry.DisplayNameEn}{VersionSuffix(entry)}"), Toasts.Kind.Ok);
                }
            }

            if (startup && loadedNow > 0)
                Toasts.Show(loadedNow == 1
                    ? Lang.T("Načten 1 mod", "1 mod loaded")
                    : Lang.T($"Načteno modů: {loadedNow}", $"{loadedNow} mods loaded"), Toasts.Kind.Ok);
            WriteStatus();
        }

        private List<ModEntry> Discover()
        {
            var result = new List<ModEntry>();
            var packaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // ids that have a package folder
            foreach (var dir in Directory.GetDirectories(_modsDir))
            {
                var manifest = Path.Combine(dir, "mod.json");
                if (!File.Exists(manifest)) continue;
                var json = ReadAllTextRetry(manifest);
                var id = JsonString(json, "id") ?? Path.GetFileName(dir);
                packaged.Add(id);
                if (File.Exists(Path.Combine(dir, "disabled"))) continue;
                var dll = JsonString(json, "dll");
                if (dll == null) continue;
                var dllPath = Path.Combine(dir, dll);
                if (!File.Exists(dllPath)) continue;
                result.Add(new ModEntry(id, JsonString(json, "name") ?? id, JsonString(json, "name_en"), JsonString(json, "version"), dllPath, manifest));
            }
            // Loose DLLs straight in Mods (development builds). A package of the same mod always wins,
            // even when it is disabled, so a leftover loose copy can't sneak back in.
            foreach (var dll in Directory.GetFiles(_modsDir, "*.dll"))
            {
                var id = Path.GetFileNameWithoutExtension(dll);
                if (packaged.Contains(id)) continue;
                result.Add(new ModEntry(id, id, null, null, dll, null));
            }
            return result;
        }

        // ---------- Loading ----------

        private bool Load(ModEntry entry)
        {
            try
            {
                var bytes = ReadAllBytesRetry(entry.DllPath);
                var pdbPath = Path.ChangeExtension(entry.DllPath, ".pdb");
                var pdb = File.Exists(pdbPath) ? ReadAllBytesRetry(pdbPath) : null;
                AssemblyRenamer.MakeUnique(ref bytes, ref pdb);
                var asm = pdb != null ? Assembly.Load(bytes, pdb) : Assembly.Load(bytes);

                var types = SafeTypes(asm).Where(t => typeof(BaseUnityPlugin).IsAssignableFrom(t) && !t.IsAbstract &&
                                                      t.GetCustomAttributes(typeof(BepInPlugin), false).Length > 0).ToList();
                if (types.Count == 0) throw new InvalidOperationException(Lang.T("v DLL není žádný BepInEx plugin", "the DLL contains no BepInEx plugin"));

                var mod = new LoadedMod(entry, asm);
                foreach (var type in types)
                {
                    var meta = (BepInPlugin)type.GetCustomAttributes(typeof(BepInPlugin), false)[0];
                    if (Chainloader.PluginInfos.ContainsKey(meta.GUID) || _loaded.Values.Any(l => l.Guids.Contains(meta.GUID)))
                    {
                        Log.LogWarning($"{entry.Name}: plugin {meta.GUID} is already loaded, skipping.");
                        continue;
                    }
                    var go = new GameObject($"SwyfMod {meta.GUID}") { hideFlags = HideFlags.HideAndDontSave };
                    DontDestroyOnLoad(go);
                    mod.Objects.Add(go);
                    mod.Guids.Add(meta.GUID);
                    go.AddComponent(type); // runs the plugin's Awake right away
                    Log.LogInfo($"Loaded {meta.Name} {meta.Version} ({meta.GUID}) from {entry.DllPath}");
                }
                if (mod.Guids.Count == 0) return false;

                _loaded[entry.Id] = mod;
                _errors.Remove(entry.Id);
                return true;
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                Log.LogError($"Failed to load {entry.Name} ({entry.DllPath}): {inner}");
                _errors[entry.Id] = entry.Stamp;
                Toasts.Show(Lang.T($"Chyba v modu {entry.Name}: {inner.Message}", $"Error in mod {entry.DisplayNameEn}: {inner.Message}"), Toasts.Kind.Error);
                return false;
            }
        }

        private void Unload(string id)
        {
            if (!_loaded.TryGetValue(id, out var mod)) return;
            _loaded.Remove(id);
            foreach (var go in mod.Objects)
            {
                try
                {
                    // Immediately, so the old instance's OnDestroy (UnpatchSelf…) runs before a new copy loads.
                    if (go != null) DestroyImmediate(go);
                }
                catch (Exception e)
                {
                    Log.LogError($"{mod.Entry.Name}: error while unloading: {e}");
                }
            }
            foreach (var guid in mod.Guids)
            {
                try
                {
                    Harmony.UnpatchID(guid);
                }
                catch (Exception e)
                {
                    Log.LogWarning($"{mod.Entry.Name}: could not remove Harmony patches of {guid}: {e.Message}");
                }
            }
            Log.LogInfo($"Unloaded {mod.Entry.Name}");
        }

        // ---------- Status file for the hub ----------

        private void WriteStatus()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"manager\":\"").Append(Version).Append("\",\"loaded\":[");
                sb.Append(string.Join(",", _loaded.Values.Select(l => $"{{\"id\":\"{Escape(l.Entry.Id)}\",\"version\":\"{Escape(l.Entry.Version ?? "")}\"}}").ToArray()));
                sb.Append("],\"errors\":[");
                sb.Append(string.Join(",", _errors.Keys.Select(k => $"\"{Escape(k)}\"").ToArray()));
                sb.Append("]}");
                File.WriteAllText(Path.Combine(Paths.BepInExRootPath, "swyf-modmanager-status.json"), sb.ToString());
            }
            catch (Exception e)
            {
                Log.LogDebug($"Status file not written: {e.Message}");
            }
        }

        // ---------- Helpers ----------

        private static string VersionSuffix(ModEntry e) => string.IsNullOrEmpty(e.Version) ? "" : $" {e.Version}";

        private static IEnumerable<Type> SafeTypes(Assembly asm)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types.Where(t => t != null);
            }
        }

        private static string JsonString(string json, string key)
        {
            var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // The hub may still be writing a file when we notice it; retry briefly.
        private static byte[] ReadAllBytesRetry(string path)
        {
            for (var i = 0; ; i++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var buffer = new byte[fs.Length];
                        var read = 0;
                        while (read < buffer.Length) read += fs.Read(buffer, read, buffer.Length - read);
                        return buffer;
                    }
                }
                catch (IOException) when (i < 10)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
        }

        private static string ReadAllTextRetry(string path) => Encoding.UTF8.GetString(ReadAllBytesRetry(path)).TrimStart('﻿');

        private sealed class ModEntry
        {
            public ModEntry(string id, string name, string nameEn, string version, string dllPath, string manifestPath)
            {
                Id = id;
                Name = name;
                NameEn = nameEn;
                Version = version;
                DllPath = dllPath;
                var dll = new FileInfo(dllPath);
                var manifest = manifestPath != null ? new FileInfo(manifestPath) : null;
                // Reload when the DLL or its manifest changes.
                Stamp = $"{dll.Length}:{dll.LastWriteTimeUtc.Ticks}:{manifest?.LastWriteTimeUtc.Ticks}";
            }

            public string Id { get; }
            public string Name { get; }      // mod.json "name" (Czech)
            public string NameEn { get; }    // mod.json "name_en", optional
            public string DisplayNameEn => string.IsNullOrEmpty(NameEn) ? Name : NameEn;
            public string Version { get; }
            public string DllPath { get; }
            public string Stamp { get; }
        }

        private sealed class LoadedMod
        {
            public LoadedMod(ModEntry entry, Assembly asm)
            {
                Entry = entry;
                Assembly = asm;
            }

            public ModEntry Entry { get; }
            public Assembly Assembly { get; }
            public List<GameObject> Objects { get; } = new List<GameObject>();
            public List<string> Guids { get; } = new List<string>();
        }
    }
}
