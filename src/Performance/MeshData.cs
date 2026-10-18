using System;
using System.Collections.Generic;
using UnityEngine;

namespace Performance
{
    /// <summary>
    /// A mesh's vertices and indices copied back from the GPU. The game's meshes are not readable (mesh.vertices /
    /// triangles throw and log a native stack per call), but the GPU buffers can be read through the engine's
    /// internal calls (see Icalls). Every attribute is decoded to floats.
    /// </summary>
    internal sealed class MeshData
    {
        public const int AttrCount = 12; // Position … TexCoord7; blend weights/indices = not a static mesh

        public int VertexCount;
        public readonly float[][] Attr = new float[AttrCount][]; // null = absent
        public readonly int[] Dim = new int[AttrCount];
        public readonly int[] Format = new int[AttrCount];
        public int[][] Indices; // per submesh, base vertex applied

        public string Signature()
        {
            var parts = new List<string>();
            for (int a = 0; a < AttrCount; a++) if (Attr[a] != null) parts.Add(a + ":" + Dim[a] + (a == 3 && Format[a] == 2 ? "c" : ""));
            return string.Join(",", parts);
        }

        /// <summary>Null + reason when the mesh can't be copied (skinned, lines, no GPU buffer…).</summary>
        public static MeshData Read(Mesh mesh, out string why)
        {
            why = null;
            if (!Icalls.Ready()) { why = "icalls: " + Icalls.Error; return null; }
            IntPtr self = Icalls.Ptr(mesh);
            if (self == IntPtr.Zero) { why = "no native mesh"; return null; }
            if (mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendWeight) || mesh.blendShapeCount > 0) { why = "skinned/blend shapes"; return null; }
            int vc = mesh.vertexCount;
            var d = new MeshData { VertexCount = vc };
            int streams = Icalls.MeshVertexBufferCount(self);
            var streamData = new byte[streams][];
            var strides = new int[streams];
            for (int a = 0; a < AttrCount; a++)
            {
                if (!mesh.HasVertexAttribute((UnityEngine.Rendering.VertexAttribute)a)) continue;
                int fmt = Icalls.AttrFormat(self, a), dim = Icalls.AttrDimension(self, a);
                int stream = Icalls.AttrStream(self, a), off = Icalls.AttrOffset(self, a);
                if (stream < 0 || stream >= streams || dim < 1 || dim > 4 || fmt < 0 || fmt > 11) { why = $"odd attribute {a}: fmt {fmt} dim {dim} stream {stream}"; return null; }
                if (streamData[stream] == null)
                {
                    strides[stream] = Icalls.MeshVertexBufferStride(self, stream);
                    streamData[stream] = ReadBuffer(Icalls.MeshVertexBuffer(self, stream), vc * strides[stream], out why);
                    if (streamData[stream] == null) return null;
                }
                var bytes = streamData[stream];
                int stride = strides[stream], cs = CompSize(fmt);
                var v = new float[vc * dim];
                for (int i = 0; i < vc; i++)
                    for (int c = 0; c < dim; c++)
                        v[i * dim + c] = Comp(bytes, i * stride + off + c * cs, fmt);
                d.Attr[a] = v;
                d.Dim[a] = dim;
                d.Format[a] = fmt;
            }
            if (d.Attr[0] == null) { why = "no positions"; return null; }

            IntPtr ib = Icalls.MeshIndexBuffer(self);
            if (ib == IntPtr.Zero) { why = "no index buffer"; return null; }
            int istride = Icalls.BufferStride(ib), icount = Icalls.BufferCount(ib);
            var ibytes = ReadBuffer(ib, istride * icount, out why);
            if (ibytes == null) return null;
            int subs = mesh.subMeshCount;
            d.Indices = new int[subs][];
            for (int s = 0; s < subs; s++)
            {
                if (Icalls.Topology(self, s) != (int)MeshTopology.Triangles) { why = "not triangles"; return null; }
                int start = (int)Icalls.IndexStart(self, s, 0), n = (int)mesh.GetIndexCount(s), bv = (int)Icalls.BaseVertex(self, s);
                if (start < 0 || start + n > icount) { why = $"index range {start}+{n} > {icount}"; return null; }
                var idx = new int[n];
                for (int i = 0; i < n; i++)
                {
                    int p = (start + i) * istride;
                    int x = (istride == 2 ? BitConverter.ToUInt16(ibytes, p) : (int)BitConverter.ToUInt32(ibytes, p)) + bv;
                    if (x < 0 || x >= vc) { why = $"index {x} out of {vc}"; return null; }
                    idx[i] = x;
                }
                d.Indices[s] = idx;
            }
            return d;
        }

        // Copies a GPU buffer into managed memory and releases the buffer handle Unity made for us.
        private static byte[] ReadBuffer(IntPtr buf, int expectBytes, out string why)
        {
            why = null;
            if (buf == IntPtr.Zero) { why = "no buffer"; return null; }
            try
            {
                int bytes = Icalls.BufferCount(buf) * Icalls.BufferStride(buf);
                if (bytes < expectBytes) { why = $"buffer {bytes} B < {expectBytes} B"; return null; }
                var data = new byte[bytes];
                Icalls.BufferGetData(buf, data, 0, 0, bytes, 1);
                return data;
            }
            catch (Exception e) { why = "GetData: " + e.Message; return null; }
            finally { Icalls.BufferDestroy(buf); }
        }

        public static int CompSize(int fmt) => fmt == 0 || fmt >= 10 ? 4 : fmt == 2 || fmt == 3 || fmt == 6 || fmt == 7 ? 1 : 2;

        private static float Comp(byte[] b, int o, int fmt)
        {
            switch (fmt)
            {
                case 0: return BitConverter.ToSingle(b, o);
                case 1: return Half(BitConverter.ToUInt16(b, o));
                case 2: return b[o] / 255f;
                case 3: return Math.Max((sbyte)b[o] / 127f, -1f);
                case 4: return BitConverter.ToUInt16(b, o) / 65535f;
                case 5: return Math.Max(BitConverter.ToInt16(b, o) / 32767f, -1f);
                case 6: return b[o];
                case 7: return (sbyte)b[o];
                case 8: return BitConverter.ToUInt16(b, o);
                case 9: return BitConverter.ToInt16(b, o);
                case 10: return BitConverter.ToUInt32(b, o);
                default: return BitConverter.ToInt32(b, o);
            }
        }

        private static float Half(ushort h)
        {
            int sign = (h >> 15) & 1, exp = (h >> 10) & 31, man = h & 1023;
            float v = exp == 0 ? man / 1024f * 6.1035156e-5f
                    : exp == 31 ? float.PositiveInfinity
                    : (1 + man / 1024f) * (float)Math.Pow(2, exp - 15);
            return sign == 1 ? -v : v;
        }
    }
}
