// ------------------------------------------------------------
// @file    ScriptCompiler.cs
// @brief   Roslyn 기반 C# 스크립트 동적 컴파일러. 파일/소스 문자열에서 DLL 바이트 배열을 생성한다.
// @deps    RoseEngine.Debug
// @exports
//   class ScriptCompiler
//     AddReference(Type type): void                                        -- 타입의 어셈블리를 참조에 추가
//     AddReference(string assemblyPath): void                              -- 어셈블리 파일 경로로 참조 추가
//     CompileFromFiles(string[] filePaths, string assemblyName): CompilationResult  -- 파일 목록으로 컴파일
//     CompileFromSource(string sourceCode, string assemblyName): CompilationResult  -- 소스 문자열로 컴파일
//     CompileFromFile(string csFilePath): CompilationResult                -- 단일 파일 컴파일
//   class CompilationResult
//     Success: bool, AssemblyBytes: byte[]?, Errors: List<string>
// @note    CompileFromFiles는 IOException 발생 시 해당 파일을 건너뛰고 경고 로그를 출력한다.
// ------------------------------------------------------------
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RoseEngine;

namespace IronRose.Scripting
{
    public class ScriptCompiler
    {
        private readonly List<MetadataReference> _references = new();

        public ScriptCompiler()
        {
            EditorDebug.Log("[Scripting] Initializing ScriptCompiler...");

            // 기본 참조 추가
            AddReference(typeof(object));           // System.Private.CoreLib
            AddReference(typeof(Console));          // System.Console
            AddReference(typeof(Enumerable));       // System.Linq

            // .NET Core/5+ 필수 참조
            var runtimeAssembly = Assembly.Load("System.Runtime");
            AddReference(runtimeAssembly.Location);

            var collectionsAssembly = Assembly.Load("System.Collections");
            AddReference(collectionsAssembly.Location);

            EditorDebug.Log("[Scripting] Added base references");

            // IronRose.Engine 참조 추가 (나중에)
            // AddReference(typeof(RoseEngine.GameObject));
        }

        public void AddReference(Type type)
        {
            _references.Add(MetadataReference.CreateFromFile(type.Assembly.Location));
        }

        public void AddReference(string assemblyPath)
        {
            if (File.Exists(assemblyPath))
            {
                _references.Add(MetadataReference.CreateFromFile(assemblyPath));
                EditorDebug.Log($"[Scripting] Added reference: {Path.GetFileName(assemblyPath)}");
            }
            else
            {
                EditorDebug.LogWarning($"[Scripting] WARNING: Assembly not found: {assemblyPath}");
            }
        }

        public CompilationResult CompileFromFiles(string[] filePaths, string assemblyName = "DynamicScript")
        {
            EditorDebug.Log($"[Scripting] CompileFromFiles: {filePaths.Length} files, assemblyName={assemblyName}", force: true);

            var syntaxTrees = new List<SyntaxTree>();
            int skippedNotFound = 0;
            int skippedIOError = 0;
            foreach (var f in filePaths)
            {
                try
                {
                    if (!File.Exists(f))
                    {
                        skippedNotFound++;
                        EditorDebug.LogWarning($"[Scripting] CompileFromFiles: file not found, skipping: {f}");
                        continue;
                    }
                    var source = File.ReadAllText(f);
                    var tree = CSharpSyntaxTree.ParseText(source, path: f,
                        encoding: System.Text.Encoding.UTF8);

                    // SyntaxTree 파싱 에러 체크
                    var parseDiags = tree.GetDiagnostics();
                    var parseErrors = parseDiags.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                    if (parseErrors.Count > 0)
                    {
                        EditorDebug.LogWarning($"[Scripting] CompileFromFiles: parse errors in {Path.GetFileName(f)} ({parseErrors.Count} errors)");
                        foreach (var pe in parseErrors)
                            EditorDebug.LogWarning($"[Scripting]   parse error: {pe.Id}: {pe.GetMessage()} at {pe.Location}");
                    }

                    syntaxTrees.Add(tree);
                    EditorDebug.Log($"[Scripting] CompileFromFiles: parsed {Path.GetFileName(f)} ({source.Length} chars)", force: true);
                }
                catch (IOException ex)
                {
                    skippedIOError++;
                    EditorDebug.LogWarning($"[Scripting] CompileFromFiles: IOException skipping {f}: {ex.Message}");
                }
            }

            EditorDebug.Log($"[Scripting] CompileFromFiles: {syntaxTrees.Count} syntax trees ready (skippedNotFound={skippedNotFound}, skippedIOError={skippedIOError})", force: true);

            return CompileFromSyntaxTrees(syntaxTrees.ToArray(), assemblyName);
        }

        public CompilationResult CompileFromSource(string sourceCode, string assemblyName = "DynamicScript")
        {
            EditorDebug.Log($"[Scripting] Compiling: {assemblyName}");

            var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode,
                encoding: System.Text.Encoding.UTF8);

            return CompileFromSyntaxTrees(new[] { syntaxTree }, assemblyName);
        }

        private CompilationResult CompileFromSyntaxTrees(SyntaxTree[] syntaxTrees, string assemblyName)
        {
            EditorDebug.Log($"[Scripting] CompileFromSyntaxTrees: {syntaxTrees.Length} trees, assemblyName={assemblyName}, {_references.Count} references", force: true);
            foreach (var r in _references)
            {
                EditorDebug.Log($"[Scripting]   reference: {r.Display}", force: true);
            }

            var compilation = CSharpCompilation.Create(
                assemblyName,
                syntaxTrees,
                _references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithOptimizationLevel(OptimizationLevel.Debug)
                    .WithAllowUnsafe(true)
            );

            using var ms = new MemoryStream();
            using var pdbStream = new MemoryStream();
            EmitResult result = compilation.Emit(ms, pdbStream);

            // Warning 로그 (성공/실패 관계없이)
            var warnings = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Warning)
                .ToList();
            if (warnings.Count > 0)
            {
                EditorDebug.Log($"[Scripting] Compilation warnings: {warnings.Count}", force: true);
                foreach (var w in warnings)
                {
                    EditorDebug.Log($"[Scripting]   warning: {w.Id}: {w.GetMessage()} at {w.Location}", force: true);
                }
            }

            if (!result.Success)
            {
                var errorDiagnostics = result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                var errors = new List<CompilationError>();
                foreach (var d in errorDiagnostics)
                {
                    var lineSpan = d.Location.GetMappedLineSpan();
                    string? errorFile = lineSpan.HasMappedPath ? lineSpan.Path : null;
                    // Roslyn LinePosition is 0-based, editor expects 1-based line numbers
                    int errorLine = lineSpan.IsValid ? lineSpan.StartLinePosition.Line + 1 : 0;

                    errors.Add(new CompilationError
                    {
                        Message = $"{d.Id}: {d.GetMessage()} at {d.Location}",
                        FilePath = errorFile,
                        Line = errorLine,
                    });
                }

                EditorDebug.LogBuildError($"[Scripting] Compilation FAILED with {errors.Count} errors");
                foreach (var error in errors)
                {
                    EditorDebug.LogBuildError($"[Scripting]   - {error.Message}",
                        sourceFile: error.FilePath, sourceLine: error.Line);
                }

                return new CompilationResult
                {
                    Success = false,
                    Errors = errors
                };
            }

            ms.Seek(0, SeekOrigin.Begin);
            pdbStream.Seek(0, SeekOrigin.Begin);
            byte[] assemblyBytes = ms.ToArray();
            byte[] pdbBytes = pdbStream.ToArray();

            EditorDebug.Log($"[Scripting] Compilation SUCCESS ({assemblyBytes.Length} bytes, PDB {pdbBytes.Length} bytes)", force: true);

            return new CompilationResult
            {
                Success = true,
                AssemblyBytes = assemblyBytes,
                PdbBytes = pdbBytes
            };
        }

        public CompilationResult CompileFromFile(string csFilePath)
        {
            EditorDebug.Log($"[Scripting] Reading file: {csFilePath}");

            if (!File.Exists(csFilePath))
            {
                return new CompilationResult
                {
                    Success = false,
                    Errors = new List<CompilationError>
                    {
                        new CompilationError { Message = $"File not found: {csFilePath}" }
                    }
                };
            }

            string sourceCode = File.ReadAllText(csFilePath);
            return CompileFromSource(sourceCode, Path.GetFileNameWithoutExtension(csFilePath));
        }
    }

    /// <summary>
    /// 컴파일 에러 하나를 나타내는 구조체. 에러 메시지와 소스 파일 위치 정보를 포함한다.
    /// </summary>
    public readonly struct CompilationError
    {
        public string Message { get; init; }
        public string? FilePath { get; init; }
        public int Line { get; init; }

        public override string ToString() => Message;
    }

    public class CompilationResult
    {
        public bool Success { get; set; }
        public byte[]? AssemblyBytes { get; set; }
        public byte[]? PdbBytes { get; set; }
        public List<CompilationError> Errors { get; set; } = new();
    }
}
