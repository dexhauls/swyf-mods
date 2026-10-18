using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SwyfModManager
{
    /// <summary>
    /// Mono hands back the already loaded assembly when Assembly.Load(byte[]) sees a name it knows, so an updated
    /// mod would silently keep running its old code. Reloads therefore get a fresh assembly name (Arcade → Arcade-r2).
    /// The first load keeps the real name.
    /// </summary>
    internal static class AssemblyRenamer
    {
        private static int _counter;

        public static void MakeUnique(ref byte[] dll, ref byte[] pdb)
        {
            string name;
            using (var probe = AssemblyDefinition.ReadAssembly(new MemoryStream(dll), new ReaderParameters { AssemblyResolver = Resolver() }))
                name = probe.Name.Name;
            if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == name)) return;

            string unique = $"{name}-r{++_counter}";
            try
            {
                Rewrite(ref dll, ref pdb, unique, withSymbols: pdb != null);
            }
            catch when (pdb != null)
            {
                // Symbols are a nice-to-have (line numbers in errors); never fail a load because of them.
                pdb = null;
                Rewrite(ref dll, ref pdb, unique, withSymbols: false);
            }
            ModManagerPlugin.Log.LogInfo($"{name} is already loaded in this session, loading the update as {unique}.");
        }

        private static DefaultAssemblyResolver _resolver;

        /// <summary>Writing an assembly can need its references (attributes, forwarded types): look in the game's folders.</summary>
        private static IAssemblyResolver Resolver()
        {
            if (_resolver != null) return _resolver;
            _resolver = new DefaultAssemblyResolver();
            foreach (var dir in new[]
            {
                BepInEx.Paths.ManagedPath, BepInEx.Paths.BepInExAssemblyDirectory, BepInEx.Paths.PluginPath,
                Path.Combine(BepInEx.Paths.BepInExRootPath, "unstripped_corlib"), Path.Combine(BepInEx.Paths.GameRootPath, "Mods"),
            })
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) _resolver.AddSearchDirectory(dir);
            return _resolver;
        }

        private static void Rewrite(ref byte[] dll, ref byte[] pdb, string newName, bool withSymbols)
        {
            var read = new ReaderParameters { AssemblyResolver = Resolver() };
            if (withSymbols)
            {
                read.ReadSymbols = true;
                read.SymbolStream = new MemoryStream(pdb);
                read.SymbolReaderProvider = new PortablePdbReaderProvider();
            }
            using (var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(dll), read))
            {
                asm.Name.Name = newName;
                var outDll = new MemoryStream();
                var outPdb = new MemoryStream();
                var write = new WriterParameters();
                if (withSymbols)
                {
                    write.WriteSymbols = true;
                    write.SymbolStream = outPdb;
                    write.SymbolWriterProvider = new PortablePdbWriterProvider();
                }
                asm.Write(outDll, write);
                dll = outDll.ToArray();
                pdb = withSymbols ? outPdb.ToArray() : null;
            }
        }
    }
}
