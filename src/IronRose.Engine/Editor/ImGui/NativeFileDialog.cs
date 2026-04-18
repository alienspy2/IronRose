// ------------------------------------------------------------
// @file    NativeFileDialog.cs
// @brief   크로스 플랫폼 OS 네이티브 파일 다이얼로그.
//          Linux: zenity / kdialog, Windows: comdlg32.dll / shell32.dll P/Invoke.
// @deps    IronRose.Engine/ProjectContext
// @exports
//   static class NativeFileDialog
//     SaveFileDialog(title, defaultName, filter, initialDir): string?  — 파일 저장 다이얼로그
//     OpenFileDialog(title, filter, initialDir): string?               — 파일 열기 다이얼로그
//     PickFolder(title, initialDir): string?                           — 폴더 선택 다이얼로그
// @note    Linux에서 zenity 우선, 없으면 kdialog 폴백.
//          Windows에서 comdlg32 GetSaveFileName/GetOpenFileName, shell32 SHBrowseForFolder 사용.
//          initialDir 미지정 시 ProjectContext.ProjectRoot 우선, 미로드 시 CWD 폴백.
//          Windows 다이얼로그는 STA(Single-Threaded Apartment) 스레드에서만 정상 작동하므로
//          호출자가 ThreadPool(MTA)에서 호출해도 내부에서 STA 전용 스레드로 마샬링된다.
// ------------------------------------------------------------
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace IronRose.Engine.Editor.ImGuiEditor
{
    /// <summary>
    /// 크로스 플랫폼 OS 네이티브 파일 다이얼로그.
    /// Linux: zenity / kdialog, Windows: comdlg32.dll P/Invoke.
    /// </summary>
    public static class NativeFileDialog
    {
        /// <summary>현재 실행 중인 네이티브 다이얼로그 프로세스 (zenity/kdialog).</summary>
        private static readonly object _processLock = new();
        private static Process? _runningProcess;

        /// <summary>
        /// 실행 중인 네이티브 다이얼로그 프로세스가 있다면 강제 종료합니다.
        /// Environment.Exit() 호출 전에 호출하여 좀비 프로세스를 방지합니다.
        /// </summary>
        public static void KillRunning()
        {
            lock (_processLock)
            {
                if (_runningProcess != null)
                {
                    try
                    {
                        if (!_runningProcess.HasExited)
                            _runningProcess.Kill();
                    }
                    catch
                    {
                        // 프로세스가 이미 종료되었거나 접근 불가 — 무시
                    }
                    _runningProcess = null;
                }
            }
        }

        /// <summary>파일 저장 다이얼로그. 취소 시 null.</summary>
        public static string? SaveFileDialog(
            string title = "Save Scene",
            string defaultName = "NewScene.scene",
            string filter = "*.scene",
            string? initialDir = null)
        {
            string? result;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                result = RunOnStaThread(() => WindowsSaveDialog(title, defaultName, filter, initialDir));
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
                return RunOnStaThread(() => WindowsOpenDialog(title, filter, initialDir));
            else
                return LinuxOpenDialog(title, filter, initialDir);
        }

        /// <summary>폴더 선택 다이얼로그. 취소 시 null.</summary>
        public static string? PickFolder(
            string title = "Select Folder",
            string? initialDir = null)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return RunOnStaThread(() => WindowsPickFolder(title, initialDir));
            else
                return LinuxPickFolder(title, initialDir);
        }

        /// <summary>
        /// Windows 다이얼로그는 STA 스레드에서만 정상 동작하므로, ThreadPool(MTA)에서
        /// 호출되더라도 항상 전용 STA 스레드에서 실행되도록 마샬링한다.
        /// </summary>
        private static string? RunOnStaThread(Func<string?> func)
        {
            if (!OperatingSystem.IsWindows())
                return func();

            string? result = null;
            Exception? captured = null;
            var thread = new Thread(() =>
            {
                try { result = func(); }
                catch (Exception e) { captured = e; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (captured != null) throw captured;
            return result;
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
            var dir = initialDir
                ?? (ProjectContext.IsProjectLoaded ? ProjectContext.ProjectRoot : Directory.GetCurrentDirectory());
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
            var dir = initialDir
                ?? (ProjectContext.IsProjectLoaded ? ProjectContext.ProjectRoot : Directory.GetCurrentDirectory());
            var label = BuildFilterLabel(filter);

            var result = RunProcess("zenity",
                $"--file-selection --title=\"{Escape(title)}\" --filename=\"{Escape(dir)}/\" --file-filter=\"{label} | {filter}\" --file-filter=\"All files | *\"");

            if (result != null) return result;

            return RunProcess("kdialog",
                $"--getopenfilename \"{Escape(dir)}\" \"{filter}\"");
        }

        private static string? LinuxPickFolder(string title, string? initialDir)
        {
            var dir = initialDir
                ?? (ProjectContext.IsProjectLoaded ? ProjectContext.ProjectRoot : Directory.GetCurrentDirectory());

            // Try zenity first
            var result = RunProcess("zenity",
                $"--file-selection --directory --title=\"{Escape(title)}\" --filename=\"{Escape(dir)}/\"");

            if (result != null) return result;

            // Fallback to kdialog
            return RunProcess("kdialog",
                $"--getexistingdirectory \"{Escape(dir)}\"");
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

                lock (_processLock)
                    _runningProcess = process;

                try
                {
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    bool exited = process.WaitForExit(30000);

                    if (!exited)
                    {
                        // 타임아웃: 좀비 프로세스 방지를 위해 강제 종료
                        try { process.Kill(); } catch { }
                        return null;
                    }

                    if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                        return null;

                    return output;
                }
                finally
                {
                    lock (_processLock)
                        _runningProcess = null;
                }
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

        // ================================================================
        // Windows — IFileOpenDialog + FOS_PICKFOLDERS (폴더 선택, Vista+)
        // 탐색기 스타일 현대 다이얼로그 (주소창/검색/즐겨찾기 지원)
        // ================================================================

        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint FOS_PATHMUSTEXIST = 0x00000800;
        private const int SIGDN_FILESYSPATH = unchecked((int)0x80058000);
        private const int ERROR_CANCELLED_HR = unchecked((int)0x800704C7);

        private static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(int sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport]
        [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            // IModalWindow
            [PreserveSig] int Show(IntPtr hwndOwner);
            // IFileDialog
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, uint fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid([In] ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            // IFileOpenDialog
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        private static string? WindowsPickFolder(string title, string? initialDir)
        {
            if (!OperatingSystem.IsWindows()) return null;

            Type? dialogType = Type.GetTypeFromCLSID(CLSID_FileOpenDialog);
            if (dialogType == null) return null;

            IFileOpenDialog? dialog = null;
            try
            {
                dialog = (IFileOpenDialog)Activator.CreateInstance(dialogType)!;
                dialog.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                dialog.SetTitle(title);

                if (!string.IsNullOrEmpty(initialDir) && Directory.Exists(initialDir))
                {
                    try
                    {
                        SHCreateItemFromParsingName(
                            initialDir, IntPtr.Zero, typeof(IShellItem).GUID, out IShellItem folderItem);
                        if (folderItem != null)
                        {
                            dialog.SetFolder(folderItem);
                            Marshal.ReleaseComObject(folderItem);
                        }
                    }
                    catch { /* initialDir 실패는 무시 — 기본 위치에서 열림 */ }
                }

                int hr = dialog.Show(IntPtr.Zero);
                if (hr == ERROR_CANCELLED_HR || hr != 0) return null;

                dialog.GetResult(out IShellItem item);
                if (item == null) return null;

                try
                {
                    item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr pszName);
                    try { return Marshal.PtrToStringUni(pszName); }
                    finally { Marshal.FreeCoTaskMem(pszName); }
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            finally
            {
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }
        }
    }
}
