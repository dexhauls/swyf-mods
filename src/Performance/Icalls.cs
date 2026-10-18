using System;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace Performance
{
    /// <summary>
    /// The game's managed Unity DLLs are stripped (no Mesh.GetVertexBuffer, GraphicsBuffer.GetData,
    /// StaticBatchingUtility…), but UnityPlayer.dll still registers every native internal call. Mono resolves an
    /// internal call by "Namespace.Class::Method" only, so a runtime-emitted class named like the engine one binds
    /// to the native function. Signatures are copied from Unity 6000.3's own bindings (Gamble With Your Friends ships
    /// an unstripped CoreModule of the same Unity version). Every "self" is the object's native pointer.
    /// </summary>
    internal static class Icalls
    {
        public static Func<IntPtr, int, IntPtr> MeshVertexBuffer;  // GraphicsBuffer*, release with BufferDestroy
        public static Func<IntPtr, IntPtr> MeshIndexBuffer;
        public static Func<IntPtr, int> MeshVertexBufferCount;
        public static Func<IntPtr, int, int> MeshVertexBufferStride;
        public static Func<IntPtr, int, int> AttrFormat, AttrDimension, AttrStream, AttrOffset; // attr → …
        public static Func<IntPtr, int, int, uint> IndexStart;    // (submesh, meshlod)
        public static Func<IntPtr, int, uint> BaseVertex;
        public static Func<IntPtr, int, int> Topology;
        public static Func<IntPtr, int> BufferCount, BufferStride;
        public static Action<IntPtr, Array, int, int, int, int> BufferGetData; // (data, managedStart, bufferStart, count, elemSize)
        public static Action<IntPtr> BufferDestroy;
        public static Action<GameObject[], IntPtr> StaticBatchCombine;
        public delegate void GetSTFn(IntPtr self, int lightmapType, out Vector4 st);
        public delegate void SetSTFn(IntPtr self, ref Vector4 st, int lightmapType);
        public static GetSTFn GetLightmapST;
        public static SetSTFn SetLightmapST;
        public static Func<IntPtr, bool> ReceiveShadows;
        public static Func<IntPtr, int> ReflectionProbeUsage;
        public delegate void BoundsFn(IntPtr self, out Bounds b);
        public static BoundsFn ProbeBounds;
        public static Func<IntPtr, int> ProbeImportance;
        public static Func<IntPtr, float> ProbeBlendDistance;
        public static string Error;

        private static readonly FieldInfo CachedPtr = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.NonPublic | BindingFlags.Instance);
        private static bool _tried;

        public static IntPtr Ptr(UnityEngine.Object o) => o == null ? IntPtr.Zero : (IntPtr)CachedPtr.GetValue(o);

        public static bool Ready()
        {
            if (_tried) return Error == null;
            _tried = true;
            try
            {
                var asm = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("VykonIcalls"), AssemblyBuilderAccess.Run);
                var mod = asm.DefineDynamicModule("VykonIcalls");
                var mesh = Begin(mod, "UnityEngine.Mesh");
                Def(mesh, "GetVertexBufferImpl_Injected", typeof(IntPtr), typeof(IntPtr), typeof(int));
                Def(mesh, "GetIndexBufferImpl_Injected", typeof(IntPtr), typeof(IntPtr));
                Def(mesh, "get_vertexBufferCount_Injected", typeof(int), typeof(IntPtr));
                Def(mesh, "GetVertexBufferStride_Injected", typeof(int), typeof(IntPtr), typeof(int));
                Def(mesh, "GetVertexAttributeFormat_Injected", typeof(int), typeof(IntPtr), typeof(int));
                Def(mesh, "GetVertexAttributeDimension_Injected", typeof(int), typeof(IntPtr), typeof(int));
                Def(mesh, "GetVertexAttributeStream_Injected", typeof(int), typeof(IntPtr), typeof(int));
                Def(mesh, "GetVertexAttributeOffset_Injected", typeof(int), typeof(IntPtr), typeof(int));
                Def(mesh, "GetIndexStartImpl_Injected", typeof(uint), typeof(IntPtr), typeof(int), typeof(int));
                Def(mesh, "GetBaseVertexImpl_Injected", typeof(uint), typeof(IntPtr), typeof(int));
                Def(mesh, "GetTopologyImpl_Injected", typeof(int), typeof(IntPtr), typeof(int));
                var buf = Begin(mod, "UnityEngine.GraphicsBuffer");
                Def(buf, "get_count_Injected", typeof(int), typeof(IntPtr));
                Def(buf, "get_stride_Injected", typeof(int), typeof(IntPtr));
                Def(buf, "InternalGetData_Injected", typeof(void), typeof(IntPtr), typeof(Array), typeof(int), typeof(int), typeof(int), typeof(int));
                Def(buf, "DestroyBuffer_Injected", typeof(void), typeof(IntPtr));
                var sb = Begin(mod, "UnityEngine.StaticBatchingHelper");
                Def(sb, "CombineMeshes_Injected", typeof(void), typeof(GameObject[]), typeof(IntPtr));
                var rend = Begin(mod, "UnityEngine.Renderer");
                Def(rend, "GetLightmapST_Injected", typeof(void), typeof(IntPtr), typeof(int), typeof(Vector4).MakeByRefType());
                Def(rend, "SetLightmapST_Injected", typeof(void), typeof(IntPtr), typeof(Vector4).MakeByRefType(), typeof(int));
                Def(rend, "get_receiveShadows_Injected", typeof(bool), typeof(IntPtr));
                Def(rend, "get_reflectionProbeUsage_Injected", typeof(int), typeof(IntPtr));
                var probe = Begin(mod, "UnityEngine.ReflectionProbe");
                Def(probe, "get_bounds_Injected", typeof(void), typeof(IntPtr), typeof(Bounds).MakeByRefType());
                Def(probe, "get_importance_Injected", typeof(int), typeof(IntPtr));
                Def(probe, "get_blendDistance_Injected", typeof(float), typeof(IntPtr));
                var pr = probe.CreateType();
                var r = rend.CreateType();
                var m = mesh.CreateType();
                var b = buf.CreateType();
                var s = sb.CreateType();
                MeshVertexBuffer = D<Func<IntPtr, int, IntPtr>>(m, "GetVertexBufferImpl_Injected");
                MeshIndexBuffer = D<Func<IntPtr, IntPtr>>(m, "GetIndexBufferImpl_Injected");
                MeshVertexBufferCount = D<Func<IntPtr, int>>(m, "get_vertexBufferCount_Injected");
                MeshVertexBufferStride = D<Func<IntPtr, int, int>>(m, "GetVertexBufferStride_Injected");
                AttrFormat = D<Func<IntPtr, int, int>>(m, "GetVertexAttributeFormat_Injected");
                AttrDimension = D<Func<IntPtr, int, int>>(m, "GetVertexAttributeDimension_Injected");
                AttrStream = D<Func<IntPtr, int, int>>(m, "GetVertexAttributeStream_Injected");
                AttrOffset = D<Func<IntPtr, int, int>>(m, "GetVertexAttributeOffset_Injected");
                IndexStart = D<Func<IntPtr, int, int, uint>>(m, "GetIndexStartImpl_Injected");
                BaseVertex = D<Func<IntPtr, int, uint>>(m, "GetBaseVertexImpl_Injected");
                Topology = D<Func<IntPtr, int, int>>(m, "GetTopologyImpl_Injected");
                BufferCount = D<Func<IntPtr, int>>(b, "get_count_Injected");
                BufferStride = D<Func<IntPtr, int>>(b, "get_stride_Injected");
                BufferGetData = D<Action<IntPtr, Array, int, int, int, int>>(b, "InternalGetData_Injected");
                BufferDestroy = D<Action<IntPtr>>(b, "DestroyBuffer_Injected");
                GetLightmapST = D<GetSTFn>(r, "GetLightmapST_Injected");
                SetLightmapST = D<SetSTFn>(r, "SetLightmapST_Injected");
                ReceiveShadows = D<Func<IntPtr, bool>>(r, "get_receiveShadows_Injected");
                ReflectionProbeUsage = D<Func<IntPtr, int>>(r, "get_reflectionProbeUsage_Injected");
                ProbeBounds = D<BoundsFn>(pr, "get_bounds_Injected");
                ProbeImportance = D<Func<IntPtr, int>>(pr, "get_importance_Injected");
                ProbeBlendDistance = D<Func<IntPtr, float>>(pr, "get_blendDistance_Injected");
                StaticBatchCombine = D<Action<GameObject[], IntPtr>>(s, "CombineMeshes_Injected");
            }
            catch (Exception e) { Error = e.ToString(); }
            return Error == null;
        }

        private static TypeBuilder Begin(ModuleBuilder mod, string name) =>
            mod.DefineType(name, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);

        private static void Def(TypeBuilder t, string name, Type ret, params Type[] args)
        {
            var m = t.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, ret, args);
            m.SetImplementationFlags(MethodImplAttributes.InternalCall);
        }

        private static T D<T>(Type t, string name) where T : Delegate =>
            (T)Delegate.CreateDelegate(typeof(T), t.GetMethod(name, BindingFlags.Public | BindingFlags.Static));
    }
}
