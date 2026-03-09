using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// P/Invoke bindings for ImGui DockBuilder functions not exposed by ImGui.NET.
    /// </summary>
    internal static class ImGuiDockBuilder
    {
        private const string CImGuiLib = "cimgui";

        [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void igDockBuilderRemoveNode(uint node_id);

        [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint igDockBuilderAddNode(uint node_id, int flags);

        [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void igDockBuilderSetNodeSize(uint node_id, Vector2 size);

        [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint igDockBuilderSplitNode(uint node_id, int split_dir,
            float size_ratio_for_node_at_dir, out uint out_id_at_dir, out uint out_id_at_opposite_dir);

        [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void igDockBuilderDockWindow(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string window_name, uint node_id);

        [DllImport(CImGuiLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void igDockBuilderFinish(uint node_id);

        // ── Public API ──

        public static void RemoveNode(uint nodeId) => igDockBuilderRemoveNode(nodeId);

        public static uint AddNode(uint nodeId, int flags = 0) => igDockBuilderAddNode(nodeId, flags);

        public static void SetNodeSize(uint nodeId, Vector2 size) => igDockBuilderSetNodeSize(nodeId, size);

        public static uint SplitNode(uint nodeId, int splitDir, float ratio,
            out uint outIdAtDir, out uint outIdAtOpposite)
            => igDockBuilderSplitNode(nodeId, splitDir, ratio, out outIdAtDir, out outIdAtOpposite);

        public static void DockWindow(string windowName, uint nodeId) => igDockBuilderDockWindow(windowName, nodeId);

        public static void Finish(uint nodeId) => igDockBuilderFinish(nodeId);

        // ImGuiDir constants (matching ImGuiNET.ImGuiDir)
        public const int DirLeft = 0;
        public const int DirRight = 1;
        public const int DirUp = 2;
        public const int DirDown = 3;

        // ImGuiDockNodeFlags
        public const int DockNodeFlagsDockSpace = 1 << 10; // ImGuiDockNodeFlags_DockSpace (internal)
    }
}
