using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// 크로스 플랫폼 OS 네이티브 파일 다이얼로그.
    /// Linux: zenity / kdialog, Windows: comdlg32.dll P/Invoke.
    /// </summary>
    public static class NativeFileDialog
    {
        /// <summary>파일 저장 다이얼로그. 취소 시 null.</summary>
        public static string? SaveFileDialog(
            string title = "Save Scene",
            string defaultName = "NewScene.scene",
            string filter = "*.scene",
            string? initialDir = null)
        {
            string? result;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                result = WindowsSaveDialog(title, defaultName, filter, initialDir);
            else
                result = LinuxSaveDialog(title, defaultName, filter, initialDir);

            string ext = ExtractExtensionFromFilter(filter);
            return EnsureExtension(result, ext);
        }

        /// <summary>파일 열기 다이얼로그. 취소 시 null.</summary>
        public static string? OpenFileDialog(
            string title = "Open Scene",
            string filter = "*.scene",
            string? initialDir = null)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return WindowsOpenDialog(title, filter, initialDir);
            else
                return LinuxOpenDialog(title, filter, initialDir);
        }

        private static string? EnsureExtension(string? path, string ext)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (!Path.HasExtension(path))
                path += ext;
            return path;
        }

        /// <summary>filter 문자열에서 첫 확장자 추출. "*.png" → ".png"</summary>
        private static string ExtractExtensionFromFilter(string filter)
        {
            var first = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "*.scene";
            if (first.StartsWith("*.") && first.Length > 2)
                return first.Substring(1); // "*.png" → ".png"
            return ".scene";
        }

        /// <summary>filter 에서 기본 확장자명 추출 (Windows lpstrDefExt 용). "*.png" → "png"</summary>
        private static string ExtractDefaultExtension(string filter)
        {
            var first = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "*.*";
            if (first.StartsWith("*.") && first.Length > 2)
                return first.Substring(2); // "*.png" → "png"
            return "";
        }

        /// <summary>
        /// generic filter → Windows OPENFILENAME 형식 변환.
        /// "*.png" → "PNG Files\0*.png\0All Files\0*.*\0"
        /// "*.png *.jpg" → "PNG Files\0*.png;*.jpg\0All Files\0*.*\0"
        /// </summary>
        private static string BuildWindowsFilter(string filter)
        {
            var patterns = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (patterns.Length == 0)
                return "All Files\0*.*\0";

            string firstExt = patterns[0].Replace("*.", "").ToUpperInvariant();
            string label = $"{firstExt} Files";
            string winPatterns = string.Join(";", patterns);

            return $"{label}\0{winPatterns}\0All Files\0*.*\0";
        }

        /// <summary>filter 에서 zenity 용 라벨 생성</summary>
        private static string BuildFilterLabel(string filter)
        {
            var first = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "*.*";
            if (first.StartsWith("*.") && first.Length > 2)
                return first.Substring(2).ToUpperInvariant() + " files";
            return "Files";
        }

        // ================================================================
        // Linux — zenity / kdialog
        // ================================================================

        private static string? LinuxSaveDialog(string title, string defaultName, string filter, string? initialDir)
        {
            var dir = initialDir ?? Directory.GetCurrentDirectory();
            var defaultPath = Path.Combine(dir, defaultName);
            var label = BuildFilterLabel(filter);

            // Try zenity first
            var result = RunProcess("zenity",
                $"--file-selection --save --confirm-overwrite --title=\"{Escape(title)}\" --filename=\"{Escape(defaultPath)}\" --file-filter=\"{label} | {filter}\" --file-filter=\"All files | *\"");

            if (result != null) return result;

            // Fallback to kdialog
            return RunProcess("kdialog",
                $"--getsavefilename \"{Escape(defaultPath)}\" \"{filter}\"");
        }

        private static string? LinuxOpenDialog(string title, string filter, string? initialDir)
        {
            var dir = initialDir ?? Directory.GetCurrentDirectory();
            var label = BuildFilterLabel(filter);

            var result = RunProcess("zenity",
                $"--file-selection --title=\"{Escape(title)}\" --filename=\"{Escape(dir)}/\" --file-filter=\"{label} | {filter}\" --file-filter=\"All files | *\"");

            if (result != null) return result;

            return RunProcess("kdialog",
                $"--getopenfilename \"{Escape(dir)}\" \"{filter}\"");
        }

        private static string? RunProcess(string command, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null) return null;

                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(30000);

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                    return null;

                return output;
            }
            catch
            {
                return null;
            }
        }

        private static string Escape(string s) => s.Replace("\"", "\\\"");

        // ================================================================
        // Windows — comdlg32.dll P/Invoke
        // ================================================================

        private const int MAX_PATH = 260;
        private const int OFN_OVERWRITEPROMPT = 0x00000002;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_NOCHANGEDIR = 0x00000008;

        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool GetSaveFileName(ref OPENFILENAME ofn);

        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct OPENFILENAME
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string? lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public string lpstrFile;
            public int nMaxFile;
            public string? lpstrFileTitle;
            public int nMaxFileTitle;
            public string? lpstrInitialDir;
            public string? lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string? lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string? lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        private static string? WindowsSaveDialog(string title, string defaultName, string filter, string? initialDir)
        {
            var fileBuffer = new string('\0', MAX_PATH);
            // Copy defaultName into the buffer
            fileBuffer = defaultName + fileBuffer.Substring(defaultName.Length);

            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                lpstrFilter = BuildWindowsFilter(filter),
                lpstrFile = fileBuffer,
                nMaxFile = MAX_PATH,
                lpstrTitle = title,
                lpstrInitialDir = initialDir,
                lpstrDefExt = ExtractDefaultExtension(filter),
                Flags = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
            };

            if (GetSaveFileName(ref ofn))
                return ofn.lpstrFile.TrimEnd('\0');

            return null;
        }

        private static string? WindowsOpenDialog(string title, string filter, string? initialDir)
        {
            var fileBuffer = new string('\0', MAX_PATH);

            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                lpstrFilter = BuildWindowsFilter(filter),
                lpstrFile = fileBuffer,
                nMaxFile = MAX_PATH,
                lpstrTitle = title,
                lpstrInitialDir = initialDir,
                lpstrDefExt = ExtractDefaultExtension(filter),
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
            };

            if (GetOpenFileName(ref ofn))
                return ofn.lpstrFile.TrimEnd('\0');

            return null;
        }
    }
}
