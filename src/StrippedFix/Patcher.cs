using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace StrippedFix
{
    /// <summary>
    /// BepInEx preloader patcher. Runs before the game loads its assemblies and adds back interface/abstract
    /// members that Unity's code stripping removed, so the game's types load with the unstripped BCL that
    /// BepInEx needs (see BepInEx\unstripped_corlib and doorstop_config.ini).
    /// </summary>
    public static class Patcher
    {
        private static readonly ManualLogSource Log = Logger.CreateLogSource("StrippedFix");
        private static StrippedTypeFixer _fixer;

        private static StrippedTypeFixer Fixer =>
            _fixer ?? (_fixer = new StrippedTypeFixer(
                Path.Combine(Paths.BepInExRootPath, "unstripped_corlib"), msg => Log.LogDebug(msg)));

        // Only assemblies that actually need stubs are targeted: BepInEx loads every targeted assembly
        // from memory, which would break code relying on Assembly.Location for the rest.
        public static IEnumerable<string> TargetDLLs
        {
            get
            {
                var sw = Stopwatch.StartNew();
                var targets = new List<string>();
                using (var resolver = new DefaultAssemblyResolver())
                {
                    resolver.AddSearchDirectory(Paths.ManagedPath);
                    foreach (var file in Directory.GetFiles(Paths.ManagedPath, "*.dll"))
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (Fixer.IsBcl(name)) continue;
                        try
                        {
                            using (var asm = AssemblyDefinition.ReadAssembly(file, new ReaderParameters { AssemblyResolver = resolver }))
                            {
                                if (Fixer.Fix(asm, apply: false) > 0) targets.Add(Path.GetFileName(file));
                            }
                        }
                        catch (BadImageFormatException)
                        {
                            // Native DLL, skip.
                        }
                        catch (Exception e)
                        {
                            Log.LogWarning($"Could not scan {name}: {e.Message}");
                        }
                    }
                }
                Log.LogInfo($"Scanned game assemblies in {sw.ElapsedMilliseconds} ms, fixing: {string.Join(", ", targets.ToArray())}");
                return targets;
            }
        }

        public static void Patch(AssemblyDefinition assembly)
        {
            // BepInEx's resolver doesn't search Managed, but writing the patched assembly may need to
            // resolve game types (e.g. enum types of parameter default values).
            if (assembly.MainModule.AssemblyResolver is BaseAssemblyResolver resolver &&
                Array.IndexOf(resolver.GetSearchDirectories(), Paths.ManagedPath) < 0)
                resolver.AddSearchDirectory(Paths.ManagedPath);

            var added = Fixer.Fix(assembly);
            Log.LogInfo($"{assembly.Name.Name}: added {added} stub member(s)");
        }
    }
}
