using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace StrippedFix
{
    /// <summary>
    /// The game ships with Unity's managed code stripping, which removed unused methods both from the
    /// BCL (mscorlib, System, ...) and from the game's own types. BepInEx needs the full BCL, but with the
    /// full BCL loaded, some stripped game types no longer implement every member of a BCL interface or
    /// abstract base class, and Mono refuses to load them (TypeLoadException: VTable setup failed).
    ///
    /// This adds the missing members back to those types as stubs that throw NotSupportedException.
    /// The game never calls them (that is why they were stripped); they only satisfy the type loader.
    /// </summary>
    public class StrippedTypeFixer
    {
        private readonly DefaultAssemblyResolver _bclResolver = new DefaultAssemblyResolver();
        private readonly HashSet<string> _bclNames;
        private readonly Action<string> _log;
        private bool _apply = true;

        public StrippedTypeFixer(string fullBclDir, Action<string> log)
        {
            foreach (var dir in _bclResolver.GetSearchDirectories()) _bclResolver.RemoveSearchDirectory(dir);
            _bclResolver.AddSearchDirectory(fullBclDir);
            _bclNames = new HashSet<string>(
                Directory.GetFiles(fullBclDir, "*.dll").Select(Path.GetFileNameWithoutExtension),
                StringComparer.OrdinalIgnoreCase);
            _log = log;
        }

        public bool IsBcl(string assemblyName) => _bclNames.Contains(assemblyName);

        /// <summary>Returns the number of stub members added (or, with apply=false, that would be added).</summary>
        public int Fix(AssemblyDefinition assembly, bool apply = true)
        {
            _apply = apply;
            var module = assembly.MainModule;
            var added = 0;
            foreach (var type in module.GetTypes().ToList())
            {
                if (type.IsInterface) continue;
                added += FixInterfaces(module, type);
                if (!type.IsAbstract) added += FixAbstractBases(module, type);
            }
            return added;
        }

        private int FixInterfaces(ModuleDefinition module, TypeDefinition type)
        {
            var added = 0;
            var queue = new Queue<TypeReference>(type.Interfaces.Select(i => i.InterfaceType));
            var seen = new HashSet<string>(type.Interfaces.Select(i => i.InterfaceType.FullName));
            while (queue.Count > 0)
            {
                var ifaceRef = queue.Dequeue();
                var elementRef = ifaceRef is GenericInstanceType git ? git.ElementType : ifaceRef;
                if (!IsBcl(elementRef.Scope.Name.Replace(".dll", ""))) continue;

                var iface = ResolveBcl(elementRef);
                if (iface == null) continue;

                // The full BCL interface may inherit interfaces that stripping cut from both the stripped
                // interface and this type's interface list (e.g. ICriticalNotifyCompletion : INotifyCompletion).
                var ifaceArgs = ifaceRef is GenericInstanceType gi ? gi.GenericArguments : null;
                foreach (var parent in iface.Interfaces)
                {
                    var parentRef = Substitute(module, parent.InterfaceType, ifaceArgs);
                    if (!seen.Add(parentRef.FullName)) continue;
                    queue.Enqueue(parentRef);
                    _log($"+ {type.FullName} : interface {parentRef.FullName}");
                    if (_apply) type.Interfaces.Add(new InterfaceImplementation(parentRef));
                }

                foreach (var m in iface.Methods)
                {
                    if (m.IsStatic || !m.IsAbstract) continue;
                    if (IsImplemented(type, m)) continue;
                    if (m.HasGenericParameters)
                    {
                        _log($"skip generic {type.FullName} : {iface.FullName}.{m.Name}");
                        continue;
                    }

                    added++;
                    _log($"+ {type.FullName} : {iface.FullName}.{m.Name}");
                    if (!_apply) continue;
                    var stub = CreateStub(module, type, $"{iface.FullName}.{m.Name}",
                        MethodAttributes.Private | MethodAttributes.Final | MethodAttributes.Virtual |
                        MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                        m, ifaceRef);
                    stub.Overrides.Add(MakeHostInstanceMethod(module.ImportReference(m), module.ImportReference(ifaceRef)));
                    type.Methods.Add(stub);
                }
            }
            return added;
        }

        private int FixAbstractBases(ModuleDefinition module, TypeDefinition type)
        {
            var added = 0;
            var baseRef = type.BaseType;
            while (baseRef != null)
            {
                var elementRef = baseRef is GenericInstanceType git ? git.ElementType : baseRef;
                var isBcl = IsBcl(elementRef.Scope.Name.Replace(".dll", ""));
                var baseDef = isBcl ? ResolveBcl(elementRef) : SafeResolve(elementRef);
                if (baseDef == null) break;

                if (isBcl)
                {
                    foreach (var m in baseDef.Methods.Where(x => x.IsAbstract && !x.IsStatic && !x.HasGenericParameters))
                    {
                        if (IsOverriddenBelow(type, baseDef, m)) continue;
                        var attrs = (m.Attributes & MethodAttributes.MemberAccessMask) | MethodAttributes.Virtual |
                                    MethodAttributes.HideBySig | MethodAttributes.ReuseSlot;
                        added++;
                        _log($"+ {type.FullName} : override {baseDef.FullName}.{m.Name}");
                        if (_apply) type.Methods.Add(CreateStub(module, type, m.Name, attrs, m, baseRef));
                    }
                }
                baseRef = baseDef.BaseType;
            }
            return added;
        }

        // Conservative: a same-named member with the same parameter count anywhere in the hierarchy
        // (implicit or explicit implementation) counts as implemented, so working code is never shadowed.
        private bool IsImplemented(TypeDefinition type, MethodDefinition ifaceMethod)
        {
            for (var t = type; t != null; t = BaseOf(t))
            {
                foreach (var m in t.Methods)
                {
                    if (m.Parameters.Count != ifaceMethod.Parameters.Count) continue;
                    if (m.Name == ifaceMethod.Name || m.Name.EndsWith("." + ifaceMethod.Name)) return true;
                }
            }
            return false;
        }

        private bool IsOverriddenBelow(TypeDefinition type, TypeDefinition declaring, MethodDefinition abstractMethod)
        {
            for (var t = type; t != null && t.FullName != declaring.FullName; t = BaseOf(t))
            {
                if (t.Methods.Any(m => m.IsVirtual && m.Name == abstractMethod.Name &&
                                       m.Parameters.Count == abstractMethod.Parameters.Count))
                    return true;
            }
            return false;
        }

        private TypeDefinition BaseOf(TypeDefinition t)
        {
            if (t.BaseType == null) return null;
            var e = t.BaseType is GenericInstanceType g ? g.ElementType : t.BaseType;
            return IsBcl(e.Scope.Name.Replace(".dll", "")) ? ResolveBcl(e) : SafeResolve(e);
        }

        private MethodDefinition CreateStub(ModuleDefinition module, TypeDefinition type, string name,
            MethodAttributes attrs, MethodDefinition template, TypeReference declaringInstance)
        {
            var args = declaringInstance is GenericInstanceType git ? git.GenericArguments : null;
            var stub = new MethodDefinition(name, attrs, Substitute(module, template.ReturnType, args));
            foreach (var p in template.Parameters)
                stub.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes & ~ParameterAttributes.HasDefault,
                    Substitute(module, p.ParameterType, args)));

            var ctor = ResolveBclType("mscorlib", "System.NotSupportedException")
                .Methods.First(m => m.IsConstructor && !m.IsStatic && m.Parameters.Count == 0);
            var il = stub.Body.GetILProcessor();
            il.Emit(OpCodes.Newobj, module.ImportReference(ctor));
            il.Emit(OpCodes.Throw);
            return stub;
        }

        // Replaces the interface/base type's generic parameters (!0, !1...) with the actual arguments.
        private static TypeReference Substitute(ModuleDefinition module, TypeReference t, IList<TypeReference> args)
        {
            switch (t)
            {
                case GenericParameter gp when gp.Type == GenericParameterType.Type && args != null:
                    return args[gp.Position];
                case ByReferenceType br:
                    return new ByReferenceType(Substitute(module, br.ElementType, args));
                case ArrayType at:
                    return new ArrayType(Substitute(module, at.ElementType, args), at.Rank);
                case PointerType pt:
                    return new PointerType(Substitute(module, pt.ElementType, args));
                case GenericInstanceType gi:
                    var inst = new GenericInstanceType(module.ImportReference(gi.ElementType));
                    foreach (var a in gi.GenericArguments) inst.GenericArguments.Add(Substitute(module, a, args));
                    return inst;
                case GenericParameter _:
                    return t;
                default:
                    return module.ImportReference(t);
            }
        }

        private static MethodReference MakeHostInstanceMethod(MethodReference m, TypeReference declaring)
        {
            var r = new MethodReference(m.Name, m.ReturnType, declaring)
            {
                HasThis = m.HasThis,
                ExplicitThis = m.ExplicitThis,
                CallingConvention = m.CallingConvention
            };
            foreach (var p in m.Parameters) r.Parameters.Add(new ParameterDefinition(p.ParameterType));
            return r;
        }

        private TypeDefinition ResolveBcl(TypeReference t)
        {
            if (t.IsNested)
            {
                var outer = ResolveBcl(t.DeclaringType);
                return outer?.NestedTypes.FirstOrDefault(n => n.Name == t.Name);
            }
            return ResolveBclType(t.Scope.Name.Replace(".dll", ""), t.FullName);
        }

        private TypeDefinition ResolveBclType(string assemblyName, string fullName)
        {
            try
            {
                var asm = _bclResolver.Resolve(new AssemblyNameReference(assemblyName, null));
                var td = asm.MainModule.GetType(fullName);
                if (td != null) return td;
                // Follow type forwarders (e.g. netstandard -> mscorlib).
                var fw = asm.MainModule.ExportedTypes.FirstOrDefault(e => e.FullName == fullName);
                return fw != null ? ResolveBclType(fw.Scope.Name.Replace(".dll", ""), fullName) : null;
            }
            catch (AssemblyResolutionException)
            {
                return null;
            }
        }

        private static TypeDefinition SafeResolve(TypeReference t)
        {
            try { return t.Resolve(); } catch { return null; }
        }
    }
}
