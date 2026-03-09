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
            Debug.Log("[Scripting] Initializing ScriptCompiler...");

            // 기본 참조 추가
            AddReference(typeof(object));           // System.Private.CoreLib
            AddReference(typeof(Console));          // System.Console
            AddReference(typeof(Enumerable));       // System.Linq

            // .NET Core/5+ 필수 참조
            var runtimeAssembly = Assembly.Load("System.Runtime");
            AddReference(runtimeAssembly.Location);

            Debug.Log("[Scripting] Added base references");

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
                Debug.Log($"[Scripting] Added reference: {Path.GetFileName(assemblyPath)}");
            }
            else
            {
                Debug.LogWarning($"[Scripting] WARNING: Assembly not found: {assemblyPath}");
            }
        }

        public CompilationResult CompileFromFiles(string[] filePaths, string assemblyName = "DynamicScript")
        {
            Debug.Log($"[Scripting] Compiling {filePaths.Length} files: {assemblyName}");

            var syntaxTrees = filePaths
                .Where(f => File.Exists(f))
                .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f))
                .ToArray();

            return CompileFromSyntaxTrees(syntaxTrees, assemblyName);
        }

        public CompilationResult CompileFromSource(string sourceCode, string assemblyName = "DynamicScript")
        {
            Debug.Log($"[Scripting] Compiling: {assemblyName}");

            var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

            return CompileFromSyntaxTrees(new[] { syntaxTree }, assemblyName);
        }

        private CompilationResult CompileFromSyntaxTrees(SyntaxTree[] syntaxTrees, string assemblyName)
        {
            var compilation = CSharpCompilation.Create(
                assemblyName,
                syntaxTrees,
                _references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithOptimizationLevel(OptimizationLevel.Debug)
                    .WithAllowUnsafe(true)
            );

            using var ms = new MemoryStream();
            EmitResult result = compilation.Emit(ms);

            if (!result.Success)
            {
                var errors = result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => $"{d.Id}: {d.GetMessage()}")
                    .ToList();

                Debug.LogError($"[Scripting] Compilation FAILED with {errors.Count} errors");
                foreach (var error in errors)
                {
                    Debug.LogError($"[Scripting]   - {error}");
                }

                return new CompilationResult
                {
                    Success = false,
                    Errors = errors
                };
            }

            ms.Seek(0, SeekOrigin.Begin);
            byte[] assemblyBytes = ms.ToArray();

            Debug.Log($"[Scripting] Compilation SUCCESS ({assemblyBytes.Length} bytes)");

            return new CompilationResult
            {
                Success = true,
                AssemblyBytes = assemblyBytes
            };
        }

        public CompilationResult CompileFromFile(string csFilePath)
        {
            Debug.Log($"[Scripting] Reading file: {csFilePath}");

            if (!File.Exists(csFilePath))
            {
                return new CompilationResult
                {
                    Success = false,
                    Errors = new List<string> { $"File not found: {csFilePath}" }
                };
            }

            string sourceCode = File.ReadAllText(csFilePath);
            return CompileFromSource(sourceCode, Path.GetFileNameWithoutExtension(csFilePath));
        }
    }

    public class CompilationResult
    {
        public bool Success { get; set; }
        public byte[]? AssemblyBytes { get; set; }
        public List<string> Errors { get; set; } = new();
    }
}
