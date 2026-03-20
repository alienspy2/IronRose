// ------------------------------------------------------------
// @file    ProjectCreator.cs
// @brief   새 프로젝트를 생성하는 정적 유틸리티.
//          templates/default/ 디렉토리를 대상 경로에 복사하고 {{ProjectName}} 플레이스홀더를 치환한다.
//          템플릿이 없으면 최소한의 프로젝트 구조를 직접 생성한다.
// @deps    ProjectContext
// @exports
//   static class ProjectCreator
//     CreateFromTemplate(string projectName, string parentDir): bool  — 템플릿 기반 프로젝트 생성
// @note    대상 디렉토리가 이미 존재하면 실패한다.
//          템플릿 디렉토리가 없으면 최소 구조(rose_config.toml, Assets/, Shaders/)를 직접 생성한다.
// ------------------------------------------------------------
using System;
using System.IO;
using RoseEngine;

namespace IronRose.Engine.Editor
{
    /// <summary>
    /// 새 프로젝트를 생성하는 정적 유틸리티.
    /// templates/default/ 디렉토리를 대상 경로에 복사하고 {{ProjectName}} 플레이스홀더를 치환한다.
    /// </summary>
    public static class ProjectCreator
    {
        /// <summary>
        /// 템플릿 기반으로 새 프로젝트를 생성합니다.
        /// 템플릿이 없으면 최소한의 프로젝트 구조를 직접 생성합니다.
        /// </summary>
        /// <param name="projectName">프로젝트 이름 (디렉토리명으로도 사용됨)</param>
        /// <param name="parentDir">프로젝트를 생성할 부모 디렉토리</param>
        /// <returns>성공 여부</returns>
        public static bool CreateFromTemplate(string projectName, string parentDir)
        {
            var targetDir = Path.Combine(parentDir, projectName);
            if (Directory.Exists(targetDir))
            {
                Debug.LogError($"[ProjectCreator] Directory already exists: {targetDir}");
                return false;
            }

            // 템플릿 경로: 엔진 루트/templates/default/
            var templateDir = Path.Combine(ProjectContext.EngineRoot, "templates", "default");
            if (Directory.Exists(templateDir))
            {
                return CreateFromTemplateDirectory(projectName, targetDir, templateDir);
            }
            else
            {
                // 템플릿이 없으면 최소 구조를 직접 생성
                return CreateMinimalProject(projectName, targetDir);
            }
        }

        private static bool CreateFromTemplateDirectory(string projectName, string targetDir, string templateDir)
        {
            try
            {
                CopyDirectory(templateDir, targetDir);

                // {{ProjectName}} 치환: 파일명
                RenameFilesWithPlaceholder(targetDir, projectName);

                // {{ProjectName}} 치환: 파일 내용
                ReplaceInFiles(targetDir, "{{ProjectName}}", projectName);

                Debug.Log($"[ProjectCreator] Project created from template: {targetDir}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ProjectCreator] Failed to create project: {ex.Message}");
                return false;
            }
        }

        private static bool CreateMinimalProject(string projectName, string targetDir)
        {
            try
            {
                Directory.CreateDirectory(targetDir);
                Directory.CreateDirectory(Path.Combine(targetDir, "Assets"));
                Directory.CreateDirectory(Path.Combine(targetDir, "Assets", "Scenes"));
                Directory.CreateDirectory(Path.Combine(targetDir, "Assets", "Settings"));

                // rose_config.toml
                var configContent = @"# RoseEngine Configuration

[cache]
dont_use_cache = false
dont_use_compress_texture = false
force_clear_cache = false
";
                File.WriteAllText(Path.Combine(targetDir, "rose_config.toml"), configContent);

                // rose_projectSettings.toml
                var settingsContent = $@"# Project Settings

[project]
name = ""{projectName}""
";
                File.WriteAllText(Path.Combine(targetDir, "rose_projectSettings.toml"), settingsContent);

                Debug.Log($"[ProjectCreator] Minimal project created: {targetDir}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ProjectCreator] Failed to create minimal project: {ex.Message}");
                return false;
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                var dest = Path.Combine(target, Path.GetFileName(file));
                File.Copy(file, dest);
            }
            foreach (var dir in Directory.GetDirectories(source))
            {
                var dest = Path.Combine(target, Path.GetFileName(dir));
                CopyDirectory(dir, dest);
            }
        }

        private static void RenameFilesWithPlaceholder(string dir, string projectName)
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Contains("{{ProjectName}}"))
                {
                    var newName = fileName.Replace("{{ProjectName}}", projectName);
                    var newPath = Path.Combine(Path.GetDirectoryName(file)!, newName);
                    File.Move(file, newPath);
                }
            }
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                RenameFilesWithPlaceholder(subDir, projectName);
            }
        }

        private static void ReplaceInFiles(string dir, string placeholder, string replacement)
        {
            // 텍스트 파일로 간주할 확장자
            string[] textExtensions = { ".toml", ".cs", ".csproj", ".sln", ".json", ".xml", ".txt", ".scene", ".md" };

            foreach (var file in Directory.GetFiles(dir))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (Array.IndexOf(textExtensions, ext) < 0) continue;

                var content = File.ReadAllText(file);
                if (content.Contains(placeholder))
                {
                    content = content.Replace(placeholder, replacement);
                    File.WriteAllText(file, content);
                }
            }
            foreach (var subDir in Directory.GetDirectories(dir))
            {
                ReplaceInFiles(subDir, placeholder, replacement);
            }
        }
    }
}
