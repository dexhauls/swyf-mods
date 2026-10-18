using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace Performance
{
    /// <summary>
    /// Windows sends WM_SETCURSOR with every mouse move and Unity answers each one with SetCursor, which measured
    /// ~8 % of the main thread while turning the camera. While the game has the cursor locked and hidden
    /// (normal play) there is nothing to set, so the message is answered here without calling Unity.
    /// The window procedure stays chained; when switched off it only passes messages through.
    /// </summary>
    internal static class CursorFilter
    {
        private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint tid, EnumProc fn, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")] private static extern IntPtr CallWindowProc(IntPtr prev, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private const int GWLP_WNDPROC = -4;
        private const uint WM_SETCURSOR = 0x0020;

        // Kept for the life of the process: Windows may call the procedure even after this mod is unloaded.
        private static WndProc _proc;
        private static IntPtr _procPtr, _prev, _hwnd;
        private static volatile bool _enabled, _swallow;
        public static long Skipped;
        public static volatile bool Paused; // A/B test: hooked, but passing everything through

        public static void SetEnabled(bool on)
        {
            _enabled = on;
            if (!on) { _swallow = false; Restore(); return; }
            if (_hwnd == IntPtr.Zero && IntPtr.Size == 8) Install();
        }

        /// <summary>Once per frame on the main thread.</summary>
        public static void Tick()
        {
            // The game (MetaCursor) locks the cursor exactly when it hides it; the visible getter is stripped.
            _swallow = _enabled && !Paused && Cursor.lockState == CursorLockMode.Locked;
        }

        private static void Install()
        {
            IntPtr found = IntPtr.Zero;
            var sb = new StringBuilder(64);
            EnumThreadWindows(GetCurrentThreadId(), (h, l) =>
            {
                sb.Length = 0;
                GetClassName(h, sb, 64);
                if (sb.ToString() == "UnityWndClass") { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero) { Plugin.Log.LogWarning("Cursor filter: game window not found."); return; }
            _proc = Proc;
            _procPtr = Marshal.GetFunctionPointerForDelegate(_proc);
            _prev = SetWindowLongPtr(found, GWLP_WNDPROC, _procPtr);
            if (_prev == IntPtr.Zero) { Plugin.Log.LogWarning("Cursor filter: could not hook the window."); return; }
            _hwnd = found;
        }

        private static void Restore()
        {
            if (_hwnd == IntPtr.Zero) return;
            // Only unhook if nobody chained after us; otherwise stay in the chain as a pass-through.
            if (GetWindowLongPtr(_hwnd, GWLP_WNDPROC) == _procPtr)
            {
                SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _prev);
                _hwnd = IntPtr.Zero;
            }
        }

        private static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // HTCLIENT only (low word of lParam == 1): over the game's own area.
            if (msg == WM_SETCURSOR && _swallow && (lParam.ToInt64() & 0xFFFF) == 1)
            {
                Skipped++;
                return new IntPtr(1);
            }
            return CallWindowProc(_prev, hwnd, msg, wParam, lParam);
        }
    }
}
