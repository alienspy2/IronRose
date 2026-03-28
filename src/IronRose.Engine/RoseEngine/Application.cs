// ------------------------------------------------------------
// @file    Application.cs
// @brief   Unity 호환 Application 정적 클래스. 엔진 상태(isPlaying, isPaused),
//          플랫폼 정보, 영속 데이터 경로, 회사/제품 이름 등을 제공한다.
// @deps    IronRose.Engine/ProjectContext
// @exports
//   static class Application
//     isPlaying: bool                   — 게임 실행 중 여부
//     isPaused: bool                    — 일시정지 여부
//     platform: string                  — OS 플랫폼 문자열
//     targetFrameRate: int              — 목표 프레임레이트 (-1=무제한)
//     companyName: string               — 회사/조직 이름 (project.toml [project] company)
//     productName: string               — 제품 이름 (ProjectContext.ProjectName)
//     persistentDataPath: string        — 영속 데이터 저장 경로 (크로스 플랫폼)
//     dataPath: string                  — 에셋 데이터 경로 (ProjectContext.AssetsPath)
//     Pause(): void                     — 엔진 일시정지
//     Resume(): void                    — 엔진 재개
//     Quit(): void                      — 엔진 종료 요청
//     InitializePaths(string, string): void — 영속 경로 초기화 (internal)
// @note    InitializePaths()는 EngineCore.InitApplication()에서 호출된다.
//          persistentDataPath는 Linux: $XDG_DATA_HOME/IronRose/{company}/{product},
//          Windows: %APPDATA%/IronRose/{company}/{product}.
// ------------------------------------------------------------
using System;
using System.IO;

namespace RoseEngine
{
    public static class Application
    {
        internal static Action? QuitAction { get; set; }
        internal static Action? PauseCallback { get; set; }
        internal static Action? ResumeCallback { get; set; }

        public static bool isPlaying { get; internal set; } = false;
        public static bool isPaused { get; internal set; } = false;
        public static string platform => Environment.OSVersion.Platform.ToString();
        public static int targetFrameRate { get; set; } = -1;

        /// <summary>회사/조직 이름. project.toml [project] company에서 읽음. 기본값 "DefaultCompany".</summary>
        public static string companyName { get; internal set; } = "DefaultCompany";

        /// <summary>제품 이름. ProjectContext.ProjectName에서 설정.</summary>
        public static string productName { get; internal set; } = "DefaultProduct";

        /// <summary>
        /// 영속 데이터 저장 경로. 크로스 플랫폼.
        /// Linux: $XDG_DATA_HOME/IronRose/{companyName}/{productName}
        ///        (XDG_DATA_HOME 미설정 시 ~/.local/share)
        /// Windows: %APPDATA%/IronRose/{companyName}/{productName}
        /// </summary>
        public static string persistentDataPath { get; internal set; } = "";

        /// <summary>
        /// 에셋 데이터 경로. ProjectContext.AssetsPath와 동일.
        /// </summary>
        public static string dataPath => IronRose.Engine.ProjectContext.AssetsPath;

        /// <summary>엔진 일시정지 토글. 입력/렌더링은 유지, 게임 로직만 중단.</summary>
        public static void Pause()
        {
            if (PauseCallback != null)
                PauseCallback();
            else
            {
                isPaused = true;
                EditorDebug.Log("[Engine] PAUSED");
            }
        }

        /// <summary>엔진 재개.</summary>
        public static void Resume()
        {
            if (ResumeCallback != null)
                ResumeCallback();
            else
            {
                isPaused = false;
                EditorDebug.Log("[Engine] PLAYING");
            }
        }

        public static void Quit()
        {
            EditorDebug.Log("[Application] Quit requested");
            QuitAction?.Invoke();
        }

        /// <summary>
        /// persistentDataPath를 크로스 플랫폼으로 결정한다.
        /// EngineCore.InitApplication()에서 호출된다.
        /// </summary>
        internal static void InitializePaths(string company, string product)
        {
            companyName = company;
            productName = product;

            string basePath;
            if (OperatingSystem.IsWindows())
            {
                // %APPDATA%/IronRose/{companyName}/{productName}
                basePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "IronRose", company, product);
            }
            else
            {
                // Linux/macOS: XDG_DATA_HOME 또는 ~/.local/share
                var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
                if (string.IsNullOrEmpty(xdgDataHome))
                    xdgDataHome = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".local", "share");
                basePath = Path.Combine(xdgDataHome, "IronRose", company, product);
            }

            persistentDataPath = basePath;
        }
    }
}
