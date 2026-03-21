// ------------------------------------------------------------
// @file    ProjectContext.cs
// @brief   프로젝트 경로 컨텍스트. 에셋 프로젝트의 루트와 엔진 루트를 관리한다.
//          project.toml에서 설정을 읽어 초기화된다.
//          글로벌 설정(last_project 등)은 ~/.ironrose/settings.toml에 저장한다.
// @deps    RoseEngine/Debug
// @exports
//   class ProjectContext (static)
//     Initialize(string?): void        -- 프로젝트 루트 탐색 및 초기화
//     SaveLastProjectPath(string): void -- 마지막 프로젝트 경로를 글로벌 설정에 저장
//     ProjectRoot: string              -- 에셋 프로젝트 루트 절대 경로
//     EngineRoot: string               -- 엔진 소스 루트 절대 경로
//     ProjectName: string              -- 프로젝트 이름 (project.toml [project] name)
//     IsProjectLoaded: bool            -- project.toml 발견 여부
//     AssetsPath: string               -- Assets/ 절대 경로
//     EditorAssetsPath: string         -- EditorAssets/ 절대 경로
//     CachePath: string                -- RoseCache/ 절대 경로
//     LiveCodePath: string             -- LiveCode/ 절대 경로
//     FrozenCodePath: string           -- FrozenCode/ 절대 경로
// @note    project.toml이 없으면 CWD를 프로젝트 루트로 폴백 (엔진 레포 직접 실행 케이스).
//          Directory.Build.props의 IronRoseRoot와 engine.path 불일치 시 경고 로그 출력.
//          A2(TomlConfig) 미완료로 Tomlyn 직접 사용 패턴 적용.
//          하위 호환: CWD의 .rose_last_project가 있으면 settings.toml로 마이그레이션 후 삭제.
//          SaveLastProjectPath()는 read-modify-write 패턴으로 기존 settings.toml의 다른 섹션을 보존한다.
// ------------------------------------------------------------
using System;
using System.IO;
using System.Xml.Linq;
using Tomlyn;
using Tomlyn.Model;
using RoseEngine;

namespace IronRose.Engine
{
    /// <summary>
    /// 프로젝트 경로 컨텍스트. 에셋 프로젝트의 루트와 엔진 루트를 관리한다.
    /// project.toml에서 설정을 읽어 초기화된다.
    /// </summary>
    public static class ProjectContext
    {
        /// <summary>에셋 프로젝트 루트 (project.toml이 있는 디렉토리).</summary>
        public static string ProjectRoot { get; private set; } = "";

        /// <summary>엔진 소스 루트 (IronRose/ 디렉토리).</summary>
        public static string EngineRoot { get; private set; } = "";

        /// <summary>프로젝트 이름 (project.toml [project] name).</summary>
        public static string ProjectName { get; private set; } = "";

        /// <summary>project.toml이 발견되어 프로젝트가 로드된 상태인지 여부.</summary>
        public static bool IsProjectLoaded { get; private set; } = false;

        /// <summary>Assets/ 절대 경로.</summary>
        public static string AssetsPath => Path.Combine(ProjectRoot, "Assets");

        /// <summary>EditorAssets/ 절대 경로 (엔진 전용, 프로젝트 접근 불가).</summary>
        internal static string EditorAssetsPath => Path.Combine(EngineRoot, "EditorAssets");

        /// <summary>RoseCache/ 절대 경로.</summary>
        public static string CachePath => Path.Combine(ProjectRoot, "RoseCache");

        /// <summary>LiveCode/ 절대 경로.</summary>
        public static string LiveCodePath => Path.Combine(ProjectRoot, "LiveCode");

        /// <summary>FrozenCode/ 절대 경로.</summary>
        public static string FrozenCodePath => Path.Combine(ProjectRoot, "FrozenCode");

        /// <summary>
        /// 프로젝트 루트를 탐색하고 project.toml을 읽어 초기화한다.
        /// </summary>
        /// <param name="projectRoot">
        /// 명시적으로 프로젝트 루트를 지정할 때 사용. null이면 자동 탐색.
        /// </param>
        public static void Initialize(string? projectRoot = null)
        {
            ProjectRoot = projectRoot
                ?? FindProjectRoot(Directory.GetCurrentDirectory())
                ?? FindProjectRoot(AppContext.BaseDirectory)
                ?? Directory.GetCurrentDirectory();

            // 정규화: 후행 슬래시 제거, 심볼릭 링크 해석
            ProjectRoot = Path.GetFullPath(ProjectRoot);

            var tomlPath = Path.Combine(ProjectRoot, "project.toml");
            if (File.Exists(tomlPath))
            {
                try
                {
                    var table = Toml.ToModel(File.ReadAllText(tomlPath));
                    // [project] name
                    if (table.TryGetValue("project", out var projVal) && projVal is TomlTable projTable)
                    {
                        if (projTable.TryGetValue("name", out var nameVal) && nameVal is string nameStr)
                            ProjectName = nameStr;
                    }

                    var engineRelPath = "../IronRose"; // 기본값
                    if (table.TryGetValue("engine", out var engineVal) && engineVal is TomlTable engineTable)
                    {
                        if (engineTable.TryGetValue("path", out var pathVal) && pathVal is string pathStr)
                            engineRelPath = pathStr;
                    }
                    EngineRoot = Path.GetFullPath(Path.Combine(ProjectRoot, engineRelPath));
                    IsProjectLoaded = true;

                    EditorDebug.Log($"[ProjectContext] Project loaded: {ProjectRoot}");
                    EditorDebug.Log($"[ProjectContext] Engine root: {EngineRoot}");

                    // Directory.Build.props와 engine.path 불일치 검증
                    ValidateBuildPropsAlignment();
                }
                catch (Exception ex)
                {
                    EditorDebug.LogWarning($"[ProjectContext] Failed to parse {tomlPath}: {ex.Message}");
                    EngineRoot = ProjectRoot;
                    IsProjectLoaded = false;
                }
            }
            else
            {
                // project.toml이 없으면 ~/.ironrose/settings.toml에서 마지막 프로젝트 경로 시도
                var lastProjectPath = ReadLastProjectPath();
                if (lastProjectPath != null)
                {
                    EditorDebug.Log($"[ProjectContext] Trying last project: {lastProjectPath}");
                    Initialize(lastProjectPath);
                    if (IsProjectLoaded) return;
                }

                // 엔진 레포 직접 실행 케이스. EngineRoot를 ProjectRoot 자신으로 폴백.
                EngineRoot = ProjectRoot;
                IsProjectLoaded = false;
                EditorDebug.Log($"[ProjectContext] No project.toml found. Fallback: ProjectRoot = EngineRoot = {ProjectRoot}");
            }
        }

        /// <summary>글로벌 설정 디렉토리 (~/.ironrose/).</summary>
        private static string GlobalSettingsDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ironrose");

        /// <summary>글로벌 설정 파일 경로 (~/.ironrose/settings.toml).</summary>
        private static string GlobalSettingsPath =>
            Path.Combine(GlobalSettingsDir, "settings.toml");

        /// <summary>레거시 설정 파일명 (하위 호환 마이그레이션용).</summary>
        private const string LEGACY_LAST_PROJECT_FILE = ".rose_last_project";

        /// <summary>
        /// ~/.ironrose/settings.toml에서 마지막 프로젝트 경로를 읽는다.
        /// settings.toml이 없으면 CWD의 .rose_last_project를 마이그레이션한다.
        /// </summary>
        private static string? ReadLastProjectPath()
        {
            // 1. ~/.ironrose/settings.toml에서 읽기
            var settingsPath = GlobalSettingsPath;
            if (File.Exists(settingsPath))
            {
                try
                {
                    var table = Toml.ToModel(File.ReadAllText(settingsPath));
                    if (table.TryGetValue("editor", out var editorVal) && editorVal is TomlTable editorTable)
                    {
                        if (editorTable.TryGetValue("last_project", out var pathVal) && pathVal is string pathStr)
                        {
                            if (!string.IsNullOrEmpty(pathStr) && File.Exists(Path.Combine(pathStr, "project.toml")))
                                return pathStr;
                        }
                    }
                }
                catch (Exception ex)
                {
                    EditorDebug.LogWarning($"[ProjectContext] Failed to parse {settingsPath}: {ex.Message}");
                }
            }

            // 2. 하위 호환: CWD의 .rose_last_project 마이그레이션
            var legacyPath = Path.Combine(Directory.GetCurrentDirectory(), LEGACY_LAST_PROJECT_FILE);
            if (File.Exists(legacyPath))
            {
                try
                {
                    var path = File.ReadAllText(legacyPath).Trim();
                    if (!string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "project.toml")))
                    {
                        SaveLastProjectPath(path);
                        try { File.Delete(legacyPath); } catch { }
                        EditorDebug.Log($"[ProjectContext] Migrated legacy .rose_last_project to settings.toml");
                        return path;
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>마지막으로 열린 프로젝트 경로를 ~/.ironrose/settings.toml에 저장한다.</summary>
        public static void SaveLastProjectPath(string projectPath)
        {
            try
            {
                Directory.CreateDirectory(GlobalSettingsDir);
                var normalizedPath = Path.GetFullPath(projectPath).Replace("\\", "/");

                // 기존 settings.toml이 있으면 읽어서 수정, 없으면 새로 생성
                TomlTable table;
                if (File.Exists(GlobalSettingsPath))
                {
                    try
                    {
                        table = Toml.ToModel(File.ReadAllText(GlobalSettingsPath));
                    }
                    catch
                    {
                        // 파싱 실패 시 새로 생성
                        table = new TomlTable();
                    }
                }
                else
                {
                    table = new TomlTable();
                }

                // [editor] 섹션 업데이트
                if (!table.TryGetValue("editor", out var editorVal) || editorVal is not TomlTable editorTable)
                {
                    editorTable = new TomlTable();
                    table["editor"] = editorTable;
                }
                editorTable["last_project"] = normalizedPath;

                File.WriteAllText(GlobalSettingsPath, Toml.FromModel(table));
                EditorDebug.Log($"[ProjectContext] Saved last project to settings: {projectPath}");
            }
            catch (Exception ex)
            {
                EditorDebug.LogWarning($"[ProjectContext] Failed to save settings: {ex.Message}");
            }
        }

        /// <summary>
        /// startDir에서 상위 디렉토리로 올라가며 project.toml을 탐색한다.
        /// </summary>
        /// <param name="startDir">탐색 시작 디렉토리.</param>
        /// <returns>project.toml이 있는 디렉토리 경로. 없으면 null.</returns>
        private static string? FindProjectRoot(string startDir)
        {
            var dir = Path.GetFullPath(startDir);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir, "project.toml")))
                    return dir;

                var parent = Path.GetDirectoryName(dir);
                // 루트 디렉토리에 도달하면 중단 (parent == dir인 경우)
                if (parent == null || parent == dir)
                    break;
                dir = parent;
            }
            return null;
        }

        /// <summary>
        /// Directory.Build.props의 IronRoseRoot 값과 project.toml의 engine.path가
        /// 동일한 경로를 가리키는지 검증한다. 불일치 시 경고 로그를 출력한다.
        /// </summary>
        private static void ValidateBuildPropsAlignment()
        {
            var propsPath = Path.Combine(ProjectRoot, "Directory.Build.props");
            if (!File.Exists(propsPath))
                return;

            try
            {
                var propsEngineRoot = ParseIronRoseRootFromProps(propsPath);
                if (propsEngineRoot == null)
                    return;

                // Directory.Build.props의 경로를 ProjectRoot 기준으로 절대 경로로 변환
                var propsAbsolute = Path.GetFullPath(Path.Combine(ProjectRoot, propsEngineRoot));

                // 후행 디렉토리 구분자 제거 후 비교
                var normalizedProps = propsAbsolute.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normalizedEngine = EngineRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (!string.Equals(normalizedProps, normalizedEngine, StringComparison.OrdinalIgnoreCase))
                {
                    EditorDebug.LogError(
                        $"[ProjectContext] engine.path ({EngineRoot}) and " +
                        $"Directory.Build.props IronRoseRoot ({propsAbsolute}) mismatch! " +
                        $"빌드/런타임 경로 불일치로 에셋 탐색 실패 가능.");
                }
            }
            catch (Exception ex)
            {
                EditorDebug.LogWarning($"[ProjectContext] Failed to validate Directory.Build.props: {ex.Message}");
            }
        }

        /// <summary>
        /// Directory.Build.props XML에서 IronRoseRoot의 기본값(MSBuild 변수 미확장)을 추출한다.
        /// </summary>
        /// <param name="propsPath">Directory.Build.props 파일 절대 경로.</param>
        /// <returns>
        /// IronRoseRoot의 기본값 문자열 (예: "../IronRose").
        /// MSBuild 변수가 포함된 경우 null 반환 (비교 불가).
        /// 파싱 실패 시에도 null 반환.
        /// </returns>
        private static string? ParseIronRoseRootFromProps(string propsPath)
        {
            var doc = XDocument.Load(propsPath);
            // <PropertyGroup> 내의 <IronRoseRoot> 요소를 찾는다.
            // Condition 없는(또는 빈 값 체크 Condition이 있는) 첫 번째 IronRoseRoot를 사용.
            foreach (var pg in doc.Descendants("PropertyGroup"))
            {
                foreach (var elem in pg.Elements("IronRoseRoot"))
                {
                    var condition = elem.Attribute("Condition")?.Value;

                    // Condition이 있는 요소가 기본값을 가진다 (빈 값일 때 설정하는 패턴)
                    // 예: <IronRoseRoot Condition="'$(IronRoseRoot)' == ''">../IronRose</IronRoseRoot>
                    if (condition != null && condition.Contains("$(IronRoseRoot)") && condition.Contains("''"))
                    {
                        var value = elem.Value.Trim();
                        // $(MSBuildThisFileDirectory) 같은 MSBuild 변수가 포함되면 런타임에서 해석 불가
                        if (value.Contains("$("))
                        {
                            // MSBuild 변수 제거 시도: $(MSBuildThisFileDirectory)는 props 파일 디렉토리
                            // 이 패턴에서는 $(MSBuildThisFileDirectory)../IronRose 형태
                            var cleaned = value.Replace("$(MSBuildThisFileDirectory)", "");
                            if (!cleaned.Contains("$("))
                                return cleaned;
                            return null;
                        }
                        return value;
                    }
                }
            }

            // Condition 없는 IronRoseRoot도 시도
            foreach (var pg in doc.Descendants("PropertyGroup"))
            {
                foreach (var elem in pg.Elements("IronRoseRoot"))
                {
                    if (elem.Attribute("Condition") == null)
                    {
                        var value = elem.Value.Trim();
                        if (!value.Contains("$("))
                            return value;
                    }
                }
            }

            return null;
        }
    }
}
