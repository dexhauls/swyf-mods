using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Performance
{
    /// <summary>
    /// Poor man's sampling profiler: a background thread pauses the game's main thread ~1000× a second and notes
    /// which DLL it is in (the instruction pointer) and which DLLs are on its stack. Split by
    /// "camera turning" / "still", it shows where the extra time goes while the mouse moves.
    /// The main thread is only read while paused; nothing is allocated meanwhile.
    /// </summary>
    internal static class StackSampler
    {
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] private static extern IntPtr OpenThread(uint access, bool inherit, uint id);
        [DllImport("kernel32.dll")] private static extern uint SuspendThread(IntPtr h);
        [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr h);
        [DllImport("kernel32.dll")] private static extern bool GetThreadContext(IntPtr h, IntPtr ctx);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr proc, IntPtr addr, IntPtr buf, IntPtr size, out IntPtr read);

        private const int StackBytes = 8192;
        private static uint _mainTid;
        private static Thread _thread;
        private static volatile bool _run;
        public static volatile int Bucket; // set by the main thread each frame

        private static string[] _names;
        private static long[] _bases, _ends;
        private static long[,] _leaf, _onStack;
        private static long[] _samples;
        // Syscall-stub leaf (win32u / ntdll) → (function, caller) histogram per bucket.
        private static Dictionary<long, int>[] _sys;
        private static Dictionary<(long, long), int>[] _pairs;
        private static int _win32u = -1, _ntdll = -1;

        public static void CaptureMainThread() => _mainTid = GetCurrentThreadId();

        public static void Start()
        {
            if (_run || _mainTid == 0 || IntPtr.Size != 8) return;
            var mods = new List<(string, long, long)>();
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
                mods.Add((m.ModuleName, m.BaseAddress.ToInt64(), m.BaseAddress.ToInt64() + m.ModuleMemorySize));
            mods.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            _names = new string[mods.Count + 1];
            _bases = new long[mods.Count];
            _ends = new long[mods.Count];
            for (int i = 0; i < mods.Count; i++) { _names[i] = mods[i].Item1; _bases[i] = mods[i].Item2; _ends[i] = mods[i].Item3; }
            _names[mods.Count] = "(JIT code / unknown)";
            _leaf = new long[2, _names.Length];
            _onStack = new long[2, _names.Length];
            _samples = new long[2];
            _sys = new[] { new Dictionary<long, int>(), new Dictionary<long, int>() };
            _pairs = new[] { new Dictionary<(long, long), int>(), new Dictionary<(long, long), int>() };
            _win32u = Array.FindIndex(_names, n => string.Equals(n, "win32u.dll", StringComparison.OrdinalIgnoreCase));
            _ntdll = Array.FindIndex(_names, n => string.Equals(n, "ntdll.dll", StringComparison.OrdinalIgnoreCase));
            _run = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "Výkon sampler", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        public static void Stop()
        {
            _run = false;
            _thread?.Join(500);
            _thread = null;
        }

        private static int Find(long addr)
        {
            int lo = 0, hi = _bases.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (addr < _bases[mid]) hi = mid - 1;
                else if (addr >= _ends[mid]) lo = mid + 1;
                else return mid;
            }
            return -1;
        }

        private static unsafe void Loop()
        {
            IntPtr h = OpenThread(0x0002 | 0x0008 | 0x0040, false, _mainTid);
            if (h == IntPtr.Zero) { _run = false; return; }
            IntPtr raw = Marshal.AllocHGlobal(1232 + 16);
            IntPtr ctx = new IntPtr((raw.ToInt64() + 15) & ~15L);
            IntPtr stack = Marshal.AllocHGlobal(StackBytes);
            IntPtr self = GetCurrentProcess();
            var seen = new bool[_names.Length];
            try
            {
                while (_run)
                {
                    Thread.Sleep(1);
                    int bucket = Bucket;
                    *(uint*)((byte*)ctx + 0x30) = 0x00100001; // CONTEXT_CONTROL
                    if (SuspendThread(h) == 0xFFFFFFFF) continue;
                    bool ok = GetThreadContext(h, ctx);
                    long rip = 0, rsp = 0;
                    IntPtr got = IntPtr.Zero;
                    if (ok)
                    {
                        rip = *(long*)((byte*)ctx + 0xF8);
                        rsp = *(long*)((byte*)ctx + 0x98);
                        ReadProcessMemory(self, new IntPtr(rsp), stack, new IntPtr(StackBytes), out got);
                    }
                    ResumeThread(h);
                    if (!ok) continue;

                    int leaf = Find(rip);
                    _leaf[bucket, leaf < 0 ? _names.Length - 1 : leaf]++;
                    Array.Clear(seen, 0, seen.Length);
                    if (leaf >= 0) seen[leaf] = true;
                    long n = got.ToInt64() / 8;
                    long* q = (long*)stack;
                    for (long i = 0; i < n; i++)
                    {
                        int m = Find(q[i]);
                        if (m >= 0) seen[m] = true;
                    }
                    for (int m = 0; m < seen.Length; m++) if (seen[m]) _onStack[bucket, m]++;
                    _samples[bucket]++;
                    if ((leaf == _win32u || leaf == _ntdll) && n > 0)
                    {
                        var key = (rip, q[0]); // syscall stubs have no frame: [rsp] is the caller
                        _pairs[bucket].TryGetValue(key, out int c);
                        _pairs[bucket][key] = c + 1;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(raw);
                Marshal.FreeHGlobal(stack);
                CloseHandle(h);
            }
        }

        // ---------- symbols from export tables ----------

        private static readonly Dictionary<int, List<(long addr, string name)>> Exports = new Dictionary<int, List<(long, string)>>();

        private static List<(long, string)> ExportsOf(int m)
        {
            if (Exports.TryGetValue(m, out var list)) return list;
            list = new List<(long, string)>();
            try
            {
                IntPtr b = new IntPtr(_bases[m]);
                int pe = Marshal.ReadInt32(b, 0x3C);
                int rva = Marshal.ReadInt32(b, pe + 0x88);
                if (rva != 0)
                {
                    IntPtr dir = new IntPtr(_bases[m] + rva);
                    int names = Marshal.ReadInt32(dir, 0x18);
                    int funcs = Marshal.ReadInt32(dir, 0x1C), nameRvas = Marshal.ReadInt32(dir, 0x20), ords = Marshal.ReadInt32(dir, 0x24);
                    for (int i = 0; i < names; i++)
                    {
                        int nameRva = Marshal.ReadInt32(new IntPtr(_bases[m] + nameRvas + i * 4));
                        int ord = (ushort)Marshal.ReadInt16(new IntPtr(_bases[m] + ords + i * 2));
                        int fn = Marshal.ReadInt32(new IntPtr(_bases[m] + funcs + ord * 4));
                        list.Add((_bases[m] + fn, Marshal.PtrToStringAnsi(new IntPtr(_bases[m] + nameRva))));
                    }
                    list.Sort((x, y) => x.Item1.CompareTo(y.Item1));
                }
            }
            catch { }
            Exports[m] = list;
            return list;
        }

        private static string Symbol(long addr)
        {
            int m = Find(addr);
            if (m < 0) return $"0x{addr:X}";
            var ex = ExportsOf(m);
            string best = null;
            long bestAddr = 0;
            foreach (var (a, name) in ex) { if (a > addr) break; best = name; bestAddr = a; }
            return best != null ? $"{_names[m]}!{best}+0x{addr - bestAddr:X}" : $"{_names[m]}+0x{addr - _bases[m]:X}";
        }

        public static string Report(string[] bucketNames)
        {
            if (_samples == null) return "";
            var sb = new StringBuilder();
            for (int b = 1; b >= 0; b--)
            {
                var top = new List<KeyValuePair<(long, long), int>>(_pairs[b]);
                top.Sort((x, y) => y.Value.CompareTo(x.Value));
                sb.AppendLine($"Top system calls ({bucketNames[b]}, % of all samples):");
                for (int i = 0; i < Math.Min(8, top.Count); i++)
                    sb.AppendLine($"  {top[i].Value * 100.0 / Math.Max(1, _samples[b]),5:F1}%  {Symbol(top[i].Key.Item1)}  <- {Symbol(top[i].Key.Item2)}");
            }
            long s0 = Math.Max(1, _samples[0]), s1 = Math.Max(1, _samples[1]);
            sb.AppendLine($"Main thread samples: {bucketNames[0]} {_samples[0]}, {bucketNames[1]} {_samples[1]}");
            sb.AppendLine($"  {"module",-34} {"leaf% " + bucketNames[0],14} {"leaf% " + bucketNames[1],14} {"stack% " + bucketNames[0],14} {"stack% " + bucketNames[1],14}");
            var idx = new List<int>();
            for (int i = 0; i < _names.Length; i++) idx.Add(i);
            idx.Sort((a, b) => Math.Max(_leaf[1, b] * 100.0 / s1, _leaf[0, b] * 100.0 / s0).CompareTo(Math.Max(_leaf[1, a] * 100.0 / s1, _leaf[0, a] * 100.0 / s0)));
            foreach (int i in idx)
            {
                double l0 = _leaf[0, i] * 100.0 / s0, l1 = _leaf[1, i] * 100.0 / s1, k0 = _onStack[0, i] * 100.0 / s0, k1 = _onStack[1, i] * 100.0 / s1;
                if (Math.Max(Math.Max(l0, l1), Math.Max(k0, k1)) < 1.0) continue;
                sb.AppendLine($"  {_names[i],-34} {l0,14:F1} {l1,14:F1} {k0,14:F1} {k1,14:F1}");
            }
            return sb.ToString();
        }
    }
}
