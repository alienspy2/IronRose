// ------------------------------------------------------------
// @file    ShaderRegistry.cs
// @brief   셰이더 경로 중앙 관리. Shaders/ 디렉토리 탐색 및 파일 경로 해석.
//          ProjectContext 초기화 이후 호출하여 엔진 루트/프로젝트 루트 기반으로 Shaders/ 위치를 결정한다.
// @deps    IronRose.Engine/ProjectContext, RoseEngine/Debug
// @exports
//   class ShaderRegistry (static)
//     Initialize(): void         -- Shaders/ 디렉토리 탐색 및 ShaderRoot 설정
//     Resolve(string): string    -- 셰이더 파일명 -> 절대 경로 변환
//     ShaderRoot: string         -- Shaders/ 절대 경로
// @note    Initialize()는 반드시 ProjectContext.Initialize() 이후에 호출해야 한다.
//          탐색 우선순위: EngineRoot/Shaders > ProjectRoot/Shaders > CWD 폴백.
//          Shaders/ 디렉토리를 찾지 못하면 DirectoryNotFoundException 발생.
// ------------------------------------------------------------
using System;
using System.IO;
using RoseEngine;

namespace IronRose.Engine
{
    /// <summary>
    /// 셰이더 경로 중앙 관리 클래스.
    /// Shaders/ 디렉토리 탐색 및 파일 경로 해석을 담당한다.
    /// </summary>
    public static class ShaderRegistry
    {
        /// <summary>Shaders/ 디렉토리 절대 경로.</summary>
        public static string ShaderRoot { get; private set; } = "";

        /// <summary>
        /// ShaderRoot를 설정한다. ProjectContext.Initialize() 이후 호출.
        /// 엔진 루트 기준으로 Shaders/ 디렉토리를 탐색한다.
        /// </summary>
        public static void Initialize()
        {
            // 1차: ProjectContext.EngineRoot 기준
            var candidate = Path.Combine(ProjectContext.EngineRoot, "Shaders");
            if (Directory.Exists(candidate))
            {
                ShaderRoot = Path.GetFullPath(candidate);
                Debug.Log($"[ShaderRegistry] Shader root: {ShaderRoot}");
                return;
            }

            // 2차: ProjectContext.ProjectRoot 기준 (엔진 레포 직접 실행 케이스)
            candidate = Path.Combine(ProjectContext.ProjectRoot, "Shaders");
            if (Directory.Exists(candidate))
            {
                ShaderRoot = Path.GetFullPath(candidate);
                Debug.Log($"[ShaderRegistry] Shader root (project): {ShaderRoot}");
                return;
            }

            // 3차: 기존 폴백 (CWD 기준 상위 탐색)
            string[] fallbacks = { "Shaders", "../Shaders", "../../Shaders" };
            foreach (var fb in fallbacks)
            {
                var fullPath = Path.GetFullPath(fb);
                if (Directory.Exists(fullPath))
                {
                    ShaderRoot = fullPath;
                    Debug.LogWarning($"[ShaderRegistry] Shader root (fallback): {ShaderRoot}");
                    return;
                }
            }

            throw new DirectoryNotFoundException(
                "[ShaderRegistry] Shaders directory not found. " +
                $"Searched: {ProjectContext.EngineRoot}/Shaders, {ProjectContext.ProjectRoot}/Shaders, CWD fallbacks");
        }

        /// <summary>
        /// 셰이더 파일명으로 절대 경로를 반환한다.
        /// PostProcessStack 등에 <c>Func&lt;string, string&gt;</c> 델리게이트로 전달 가능.
        /// </summary>
        /// <param name="fileName">셰이더 파일명 (예: "vertex.glsl", "bloom_threshold.frag")</param>
        /// <returns>셰이더 파일 절대 경로</returns>
        public static string Resolve(string fileName)
        {
            return Path.Combine(ShaderRoot, fileName);
        }
    }
}
