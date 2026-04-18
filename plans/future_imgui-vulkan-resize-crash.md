# ImGui/Vulkan 리사이즈 중 간헐적 치명적 크래시 조사

> **상태**: 원인 미확정, 재현 비결정적. 수집된 증거만 기록.
> **작성일**: 2026-04-19

---

## 증상

- 에디터 창이 멀쩡히 떠 있으나 **모든 입력이 막힘**(씬뷰/메뉴/Hierarchy/Project 클릭 전부 무반응)
- **창 크기 변경(OS 경계 드래그)도 안 됨**
- 프로세스는 계속 CPU를 먹고 메모리가 폭주함

## 프로세스 상태 관찰 (PID 18852, dotnet.exe)

두 번에 걸쳐 측정:

| 시점 | CPU 누적 | WorkingSet | Responding |
|------|---------|-----------|-----------|
| 1차 | 135초 | 2.7 GB | True |
| 2차(수 분 후) | 310초 | **5.65 GB** | **False** |

- 메인 루프는 회전 중(CPU 증가)
- 메모리 무제한 증가 중 → OOM으로 향하는 진행성 상태
- 일정 시간 지나면 OS가 창을 unresponsive로 판정

## 치명적 예외 (사용자 콘솔에 출력된 Fatal error)

```
Fatal error.
0xC0000005
   at Vulkan.VulkanNative.vkMapMemory(...)
   at Veldrid.Vk.VkDeviceMemoryManager+ChunkAllocator..ctor(...)
   at Veldrid.Vk.VkDeviceMemoryManager+ChunkAllocatorSet.Allocate(...)
   at Veldrid.Vk.VkDeviceMemoryManager.Allocate(...)
   at Veldrid.Vk.VkBuffer..ctor(...)
   at Veldrid.Vk.VkResourceFactory.CreateBufferCore(...)
   at Veldrid.ResourceFactory.CreateBuffer(...)
   at Veldrid.Vk.VkCommandList.GetStagingBuffer(UInt32)
   at Veldrid.Vk.VkCommandList.UpdateBufferCore(...)
   at Veldrid.CommandList.UpdateBuffer(...)
   at IronRose.Engine.Editor.ImGuiEditor.VeldridImGuiRenderer.Render(CommandList, ImDrawDataPtr)
   at IronRose.Engine.Editor.ImGuiEditor.ImGuiOverlay.Render(CommandList)
   at IronRose.Engine.EngineCore.Render()
   at IronRose.RoseEditor.Program.OnRender(Double)
   at Silk.NET.Windowing.Internals.ViewImplementationBase.DoRender()
   ...
```

### 확정 사항

- **0xC0000005** = Windows SEH Access Violation (순수 native 크래시)
- 크래시 지점: `vkMapMemory` 내부
- Managed 쪽 도달 지점: [`VeldridImGuiRenderer.Render`](../src/IronRose.Engine/Editor/ImGui/VeldridImGuiRenderer.cs#L227) line 227 (`cl.UpdateBuffer(_vertexBuffer, vtxOffset, cmdList.VtxBuffer.Data, vtxSize)`)
- Veldrid가 **staging buffer용 신규 VkBuffer 생성 + 신규 memory chunk 할당**을 하는 경로에서 AV 발생
- 별개로 IDE에서 관측된 예외는 **`System.ExecutionEngineException`** — CLR runtime 자체 손상 신호 (`<Cannot evaluate the exception stack trace>`)

### 콜스택 덤프 자료

- 미니덤프: [logs/ir_18852.dmp](../logs/ir_18852.dmp) (14 MB)
- 덤프 분석 결과: [logs/dump_analyze.txt](../logs/dump_analyze.txt)
- 덤프로 뽑은 메인 스레드(OS TID 0x4b20) 스택은 위 Fatal error 스택과 **완전 동일**

## 크래시 직전 로그 (시간순)

```
[LOG] [GBuffer] Initialized (1920x1009)
[LOG] [SceneView] RT bound: 1459x691
Fatal error. 0xC0000005 ...
```

이 두 줄이 같은 프레임 묶음에서 연달아 찍힘.

## 재현 시도 (2026-04-19 00:10 세션)

- 로그: [logs/editor_20260419_001023.log](../logs/editor_20260419_001023.log) (294줄, 정상 종료)
- Scene View를 좌우로 **100회 이상** 드래그 리사이즈(폭 368~1482px 범위)
- 크래시 **재현 안 됨**

### 두 세션의 로그 차이

| 항목 | 크래시 세션 | 정상 세션 |
|------|-----------|----------|
| 리사이즈 이벤트 수 | ~10회 | 100회+ |
| `[GBuffer] Initialized` | 리사이즈 **도중** 발생 | 초기 4회만, 리사이즈 중엔 없음 |
| `[SceneView] RT bound` | 리사이즈 중 | 리사이즈 중 (동일) |
| 결과 | Fatal AV | 정상 shutdown |

→ **Scene View 패널 리사이즈만으로는 재현되지 않는다.** 크래시 세션에서는 리사이즈 도중 `[GBuffer] Initialized`가 나타났는데, 정상 세션에서는 초기화 후 리사이즈 동안 GBuffer 재생성이 일어나지 않았다. 즉 크래시의 트리거에는 **GBuffer 재초기화 경로**가 개입할 가능성이 높다 (아직 확정 아님).

## 사실 vs 미확정 구분

### 확정
- 크래시는 native AV (`vkMapMemory` 내부)
- 크래시 시점 managed 호출은 `VeldridImGuiRenderer.Render`의 `cl.UpdateBuffer`
- 크래시 직전 `[GBuffer] Initialized` + `[SceneView] RT bound` 로그가 연속
- 메모리 폭주(2.7→5.65 GB) 관측
- Scene View 리사이즈만으로는 재현 안 됨

### 미확정 (가설 단계)
- GBuffer 재초기화가 원인인지 (상관관계 1건 관측, 인과 확인 필요)
- Veldrid memory allocator metadata 오염 vs 드라이버 버그
- 어떤 커밋이 regression을 도입했는지 (최근 `3dfa7ff`, `76db7d6` 의심이나 확정 아님)
- 멀티스레드 개입 여부 (CLI/FSW/Task가 GPU 리소스 건드리는 경로)

## 조사 재개 시 우선 할 일

1. **다른 조합 재현 시도**
   - 본창(에디터 윈도우) 자체 리사이즈 + Scene View 리사이즈 섞기
   - Game View 패널 리사이즈
   - 플레이 모드 진입/종료 반복 중 리사이즈
   - 씬 로드 직후 즉시 리사이즈
2. **재현되면 즉시**
   - `dotnet-dump collect -p <pid> -o logs/ir_<pid>.dmp --type Mini`
   - `dotnet-dump analyze` 로 모든 스레드 `clrstack` 확인 (ThreadPool/FSW/CLI 스레드가 GPU 리소스 접근 중인지 체크)
3. **정적 조사 포인트**
   - `[GBuffer] Initialized` 로그를 찍는 위치 확인(본창/게임뷰 리사이즈 핸들러?)
   - 해당 경로의 Dispose/Recreate 순서가 `WaitForIdle` 또는 fence 동기화 뒤에 오는지
   - `_vertexBuffer`/`_indexBuffer` grow 경로 ([VeldridImGuiRenderer.cs:205-216](../src/IronRose.Engine/Editor/ImGui/VeldridImGuiRenderer.cs#L205)) 의 Dispose 직후 생성이 GPU 인플라이트 리소스와 충돌할 여지
4. **방어적 로그**
   - GBuffer/SceneViewRT Dispose/Create 지점에 타임스탬프 로그
   - `ThreadGuard.CheckMainThread`를 해당 경로에 명시적으로 심어 비-메인 호출 발생 여부 확인
