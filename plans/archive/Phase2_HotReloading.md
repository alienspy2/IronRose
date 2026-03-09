# Phase 2: Roslyn 핫 리로딩 시스템

## 목표
런타임에 C# 코드를 컴파일하고 AssemblyLoadContext로 핫 리로딩하는 핵심 기능을 구현합니다.

---

## 설계 철학: 플러그인 기반 핫 리로드

### IronRose 아키텍처
```
[IronRose.Engine (EXE, 안정적 기반)]
  ├─ [Plugin DLLs (ALC 핫 리로드)] ← 게임 로직, 커스텀 기능
  └─ [LiveCode/*.cs (Roslyn 핫 리로드)] ← 빠른 프로토타입
```
- ✅ 엔진 코어는 안정적으로 유지
- ✅ 플러그인/LiveCode만 핫 리로드
- ✅ AI Digest로 검증된 코드를 엔진에 통합

---

## 작업 항목

### 2.1 Roslyn 컴파일러 래퍼 (IronRose.Scripting)

**ScriptCompiler.cs:**
```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace IronRose.Scripting
{
    public class ScriptCompiler
    {
        private readonly List<MetadataReference> _references = new();

        public ScriptCompiler()
        {
            // 기본 참조 추가
            AddReference(typeof(object));           // System.Private.CoreLib
            AddReference(typeof(Console));          // System.Console
            AddReference(typeof(Enumerable));       // System.Linq

            // IronRose.Engine 참조 추가 (나중에)
            // AddReference(typeof(RoseEngine.GameObject));
        }

        public void AddReference(Type type)
        {
            _references.Add(MetadataReference.CreateFromFile(type.Assembly.Location));
        }

        public void AddReference(string assemblyPath)
        {
            _references.Add(MetadataReference.CreateFromFile(assemblyPath));
        }

        public CompilationResult CompileFromSource(string sourceCode, string assemblyName = "DynamicScript")
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { syntaxTree },
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

                return new CompilationResult
                {
                    Success = false,
                    Errors = errors
                };
            }

            ms.Seek(0, SeekOrigin.Begin);
            byte[] assemblyBytes = ms.ToArray();

            return new CompilationResult
            {
                Success = true,
                AssemblyBytes = assemblyBytes
            };
        }

        public CompilationResult CompileFromFile(string csFilePath)
        {
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
```

---

### 2.2 AssemblyLoadContext 핫 스왑 구조

**ScriptDomain.cs:**
```csharp
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;

namespace IronRose.Scripting
{
    public class ScriptDomain
    {
        private AssemblyLoadContext? _currentALC;
        private Assembly? _currentAssembly;
        private readonly List<object> _scriptInstances = new();

        public void LoadScripts(byte[] assemblyBytes)
        {
            // 새로운 ALC 생성
            _currentALC = new AssemblyLoadContext($"ScriptContext_{DateTime.Now.Ticks}", isCollectible: true);

            // 어셈블리 로드
            using var ms = new System.IO.MemoryStream(assemblyBytes);
            _currentAssembly = _currentALC.LoadFromStream(ms);

            // 스크립트 클래스 인스턴스화
            InstantiateScripts();

            Console.WriteLine($"[ScriptDomain] Loaded assembly: {_currentAssembly.FullName}");
        }

        public void Reload(byte[] newAssemblyBytes)
        {
            Console.WriteLine("[ScriptDomain] Hot reloading scripts...");

            // 기존 상태 저장 (TODO: Phase 2.3에서 구현)
            // SaveState();

            // 기존 ALC 언로드
            UnloadPreviousContext();

            // 새 어셈블리 로드
            LoadScripts(newAssemblyBytes);

            // 상태 복원 (TODO: Phase 2.3에서 구현)
            // RestoreState();

            Console.WriteLine("[ScriptDomain] Hot reload completed!");
        }

        private void UnloadPreviousContext()
        {
            if (_currentALC == null) return;

            _scriptInstances.Clear();
            _currentAssembly = null;

            var weakRef = new WeakReference(_currentALC, trackResurrection: true);
            _currentALC.Unload();
            _currentALC = null;

            // GC 강제 실행
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            if (weakRef.IsAlive)
            {
                Console.WriteLine("[ScriptDomain] WARNING: ALC not fully unloaded!");
            }
        }

        private void InstantiateScripts()
        {
            if (_currentAssembly == null) return;

            foreach (var type in _currentAssembly.GetTypes())
            {
                // Update() 메서드가 있는 클래스만 인스턴스화
                if (type.GetMethod("Update") != null)
                {
                    var instance = Activator.CreateInstance(type);
                    if (instance != null)
                    {
                        _scriptInstances.Add(instance);
                        Console.WriteLine($"[ScriptDomain] Instantiated: {type.Name}");
                    }
                }
            }
        }

        public void Update()
        {
            foreach (var instance in _scriptInstances)
            {
                var updateMethod = instance.GetType().GetMethod("Update");
                updateMethod?.Invoke(instance, null);
            }
        }
    }
}
```

---

### 2.3 상태 보존 시스템

**IHotReloadable.cs:**
```csharp
namespace IronRose.Scripting
{
    public interface IHotReloadable
    {
        string SerializeState();    // TOML 형식으로 반환
        void DeserializeState(string toml);
    }
}
```

**StateManager.cs (간단한 구현):**
```csharp
using System.Collections.Generic;
using Tomlyn;

namespace IronRose.Scripting
{
    public class StateManager
    {
        private Dictionary<string, string> _savedStates = new();

        public void SaveStates(List<object> instances)
        {
            _savedStates.Clear();

            foreach (var instance in instances)
            {
                if (instance is IHotReloadable reloadable)
                {
                    string typeName = instance.GetType().FullName!;
                    string state = reloadable.SerializeState();
                    _savedStates[typeName] = state;
                }
            }
        }

        public void RestoreStates(List<object> instances)
        {
            foreach (var instance in instances)
            {
                if (instance is IHotReloadable reloadable)
                {
                    string typeName = instance.GetType().FullName!;
                    if (_savedStates.TryGetValue(typeName, out string? state))
                    {
                        reloadable.DeserializeState(state);
                    }
                }
            }
        }
    }
}
```

---

### 2.4 테스트: "Hello World" 스크립트

**LiveCode/TestScript.cs (외부 파일):**
```csharp
using System;

public class TestScript
{
    private int _frameCount = 0;

    public void Update()
    {
        _frameCount++;
        if (_frameCount % 60 == 0)
        {
            Console.WriteLine($"[TestScript] Frame: {_frameCount}");
        }
    }
}
```

**Program.cs 통합:**
```csharp
private static ScriptCompiler _compiler = null!;
private static ScriptDomain _scriptDomain = null!;
private static FileSystemWatcher _watcher = null!;

static void InitializeScripting()
{
    _compiler = new ScriptCompiler();
    _scriptDomain = new ScriptDomain();

    // 초기 컴파일
    CompileAndLoadScripts("LiveCode/TestScript.cs");

    // 파일 변경 감시
    _watcher = new FileSystemWatcher("LiveCode", "*.cs");
    _watcher.Changed += OnScriptFileChanged;
    _watcher.EnableRaisingEvents = true;
}

static void CompileAndLoadScripts(string filePath)
{
    var result = _compiler.CompileFromFile(filePath);

    if (result.Success)
    {
        _scriptDomain.LoadScripts(result.AssemblyBytes!);
    }
    else
    {
        Console.WriteLine("[Compiler] Errors:");
        foreach (var error in result.Errors)
        {
            Console.WriteLine($"  - {error}");
        }
    }
}

static void OnScriptFileChanged(object sender, FileSystemEventArgs e)
{
    Console.WriteLine($"[FileWatcher] Detected change: {e.Name}");
    System.Threading.Thread.Sleep(100); // 파일 쓰기 완료 대기

    var result = _compiler.CompileFromFile(e.FullPath);
    if (result.Success)
    {
        _scriptDomain.Reload(result.AssemblyBytes!);
    }
}

static void MainLoop()
{
    while (_running)
    {
        // ... 이벤트 처리 ...

        // 스크립트 업데이트
        _scriptDomain.Update();

        // 렌더링
        _graphics.Render();
    }
}
```

---

## 검증 기준

✅ TestScript.cs 컴파일 및 로드 성공
✅ 매 프레임 Update() 메서드 호출
✅ TestScript.cs 수정 → 저장하면 자동 리로드
✅ 핫 리로드 중에도 게임 루프 중단 없음
✅ 콘솔에 "Frame: 60, 120, 180..." 출력

---

## 테스트 시나리오

1. 엔진 실행
2. 콘솔에 "Frame: 60" 출력 확인
3. TestScript.cs의 출력 메시지 변경:
   ```csharp
   Console.WriteLine($"HOT RELOAD TEST! Frame: {_frameCount}");
   ```
4. 파일 저장
5. 콘솔에 "[ScriptDomain] Hot reload completed!" 출력 확인
6. 새로운 메시지 출력 확인

---

## 예상 소요 시간
**3-4일**

---

## 다음 단계
→ [Phase 3: Unity Architecture 구현](Phase3_UnityArchitecture.md)
