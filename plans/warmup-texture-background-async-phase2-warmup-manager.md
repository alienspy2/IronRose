---
name: Phase 2 — AssetWarmupManager 텍스처 비동기 파이프라인
type: plan
parent: plans/warmup-texture-background-async.md
scope: src/IronRose.Engine/AssetWarmupManager.cs
depends_on: plans/warmup-texture-background-async-phase1-rosecache-split.md
status: draft
---

# Phase 2 — `AssetWarmupManager` 텍스처 Task.Run 전환

> 상위: [warmup-texture-background-async.md](warmup-texture-background-async.md)
> 선행: [Phase 1](warmup-texture-background-async-phase1-rosecache-split.md) — `RoseCache`의 Plan/Background/Finalize API 준비 완료.
> 선행: [Phase 3](warmup-texture-background-async-phase3-asset-database-api.md) — `AssetDatabase.PrepareTextureWarmupBackground` / `FinalizeTextureWarmupOnMain` API 추가.
> **Phase 2와 3은 같은 worktree에서 함께 구현**한다 (API 계약 공유). 문서만 구분.

---

## 다루는 변경

`AssetWarmupManager.ProcessFrame`의 상태 머신을 확장하여 텍스처 에셋도 **백그라운드 디코드/압축 → 다음 프레임 메인 GPU 마무리** 흐름으로 처리한다.

현재는 메시만 `Task.Run`이고 텍스처는 메인 동기. 변경 후에는 둘 다 백그라운드 Task로 시작하되, 텍스처는 추가로 **메인 프레임에서 마무리 단계**를 거친다.

---

## 대상 파일 / 라인

### 주 수정: `src/IronRose.Engine/AssetWarmupManager.cs`

| 구간 | 현재 라인 | 변경 |
|------|-----------|------|
| 필드 | 22 | `_backgroundTask`만 존재 → `_meshBackgroundTask`, `_textureBackgroundTask` 분리. 또는 단일 `Task`로 유지하되 handoff 값의 타입으로 분기. |
| `ProcessFrame` | 58-105 | 상태 머신 재작성. 3상태: Idle / BackgroundRunning / TextureFinalizing. |
| `IsMeshAsset` | 107-111 | 변경 없음. |
| `Finish` | 113-124 | 변경 없음. |

### 참조 파일 (읽기 전용)
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` — Phase 3에서 신설되는 `PrepareTextureWarmupBackground` / `FinalizeTextureWarmupOnMain` 사용.
- `src/IronRose.Engine/AssetPipeline/TextureWarmupTypes.cs` — `WarmupHandoff` record.

---

## 상태 머신 설계

### 필드 변경

**현재**:
```csharp
private Task? _backgroundTask;
```

**변경 후** (옵션 A — 단일 Task, 내부 결과 타입으로 분기):
```csharp
// 메시 워밍업: Task는 void, 내부에서 _assetDatabase.EnsureDiskCached 동기 호출.
private Task? _meshBackgroundTask;

// 텍스처 워밍업: Task<WarmupHandoff> 반환. 메인에서 Result 소비 후 Finalize.
private Task<WarmupHandoff>? _textureBackgroundTask;
```

**옵션 B** (단일 discriminated Task):
```csharp
// Task.AsyncState로 에셋 타입 구분.
private Task? _activeTask;
private WarmupHandoff? _pendingFinalize;
```

**권장**: 옵션 A. 타입이 명시적이고 리뷰하기 쉽다.

### `ProcessFrame` 새 로직

```csharp
public void ProcessFrame()
{
    if (_warmUpQueue == null) { Finish(); return; }

    // 1. 진행 중 Task 대기
    if (_meshBackgroundTask != null)
    {
        if (!_meshBackgroundTask.IsCompleted) return;
        if (_meshBackgroundTask.IsFaulted)
        {
            var ex = _meshBackgroundTask.Exception?.InnerException;
            EditorDebug.LogError($"[Engine] Warm-up (mesh) failed for {CurrentAssetName}: {ex?.Message}");
        }
        _meshBackgroundTask = null;
        _warmUpNext++;
    }
    else if (_textureBackgroundTask != null)
    {
        if (!_textureBackgroundTask.IsCompleted) return;

        WarmupHandoff handoff;
        if (_textureBackgroundTask.IsFaulted)
        {
            var ex = _textureBackgroundTask.Exception?.InnerException;
            EditorDebug.LogError($"[Engine] Warm-up (texture, bg) failed for {CurrentAssetName}: {ex?.Message}");
            _textureBackgroundTask = null;
            _warmUpNext++;
        }
        else
        {
            handoff = _textureBackgroundTask.Result;
            _textureBackgroundTask = null;

            // 메인에서 GPU 마무리 + 디스크 저장 + sub-asset 등록.
            try
            {
                _assetDatabase.FinalizeTextureWarmupOnMain(handoff);
            }
            catch (Exception ex)
            {
                EditorDebug.LogError($"[Engine] Warm-up (texture, finalize) failed for {CurrentAssetName}: {ex.Message}");
            }
            _warmUpNext++;
        }
    }

    // 2. 다음 에셋 큐잉
    if (_warmUpNext >= _warmUpQueue.Length)
    {
        Finish();
        return;
    }

    var path = _warmUpQueue[_warmUpNext];
    CurrentAssetName = Path.GetFileName(path);

    if (IsMeshAsset(path))
    {
        _meshBackgroundTask = Task.Run(() => _assetDatabase.EnsureDiskCached(path));
    }
    else
    {
        // 텍스처(+폰트 등): 백그라운드 디코드/CLI 압축.
        // 폰트 같은 non-texture 에셋은 PrepareTextureWarmupBackground 내부에서
        // importerType 체크 후 skip-handoff 반환하면 다음 프레임에 즉시 _warmUpNext++.
        _textureBackgroundTask = Task.Run(() => _assetDatabase.PrepareTextureWarmupBackground(path));
    }
}
```

### 예외 안전성

- Task.Run 람다 안에서 throw되면 Task.IsFaulted가 true. `handoff` 값은 사용 불가 — LogError 후 스킵.
- `PrepareTextureWarmupBackground`는 내부에서 **예외를 잡아 `handoff.Error`에 저장**하도록 구현(Phase 3에서 상세). 이 경우 Task.IsFaulted는 false이고 handoff.Error만 세팅됨. `FinalizeTextureWarmupOnMain` 내부에서 Error != null이면 조용히 스킵.
- Task 자체가 예외로 깨진 경우(예: OOM)는 `IsFaulted` 분기에서 처리.

### 프로그레스 UI

`CurrentAssetName` / `CurrentIndex` / `TotalCount` / `ElapsedSeconds`는 변경 없음. 프레임당 하나씩 처리되는 구조 유지 → 오버레이 표시 로직 그대로 동작.

---

## 호환성 고려

### non-mesh / non-texture 에셋 처리
현재 `IsMeshAsset`이 false면 모두 "메인 동기" 경로였다. 실제 warmup 큐에 올라가는 것은 `GetUncachedAssetPaths` (AssetDatabase.cs line 572-593)가 `importerType is "MeshImporter" or "TextureImporter"`인 것만 모으므로, **실질적으로 mesh + texture 2종만 존재**한다.

따라서 `IsMeshAsset == false`는 곧 TextureImporter임을 가정해도 안전. 단 방어적으로:
- Phase 3의 `PrepareTextureWarmupBackground`가 importerType이 TextureImporter가 아니면 "skip" 플래그 handoff를 반환하도록 구현.
- `FinalizeTextureWarmupOnMain`에서 skip 플래그면 아무 작업 없이 return.

### 플레이모드 진입 방지
`EngineCore`는 warmup 중에는 플레이모드 진입을 막는다 (기존 동작, `EngineCore.cs:365` 근처 `IsWarmingUp` 체크). 변경 없음.

### `ReimportAsync`와의 충돌
- `ReimportAsync`는 `_reimportTask`를 사용.
- Warmup은 `_meshBackgroundTask` / `_textureBackgroundTask`를 사용.
- 둘이 동시에 돌아도 서로 다른 필드 + 메인 프레임에서 각각 소비되므로 충돌 없음.
- 단 `FinalizeTextureWarmupOnMain`이 `_loadedAssets`에 등록하지 **않음** (warmup 특성상 "in-memory 없이 디스크만 캐시"). 따라서 `ReimportAsync`와 자료 경합 없음.

---

## ThreadGuard 삽입 위치

`AssetWarmupManager` 자체에는 ThreadGuard 삽입 불필요 (`ProcessFrame`은 `EngineCore.Update`에서만 호출 → 자동으로 메인). 단 방어적으로 진입부에 `ThreadGuard.DebugCheckMainThread("AssetWarmupManager.ProcessFrame")` 삽입 고려.

실질적 가드는 Phase 3(`AssetDatabase.FinalizeTextureWarmupOnMain`)와 Phase 1(`RoseCache.FinalizeTextureOnMain`, `StoreTexturePrecompressed`)에서 담당.

---

## 실패 시나리오 & 폴백

| 시나리오 | 기대 동작 |
|----------|-----------|
| 백그라운드 Task가 예외로 crash | LogError + `_warmUpNext++`. 다음 에셋 진행. |
| `WarmupHandoff.Error != null` (예: 파일 없음) | `FinalizeTextureWarmupOnMain`에서 LogWarning + skip. 다음 에셋. |
| Warmup 중 에디터 종료 | `_meshBackgroundTask` / `_textureBackgroundTask`는 Task이므로 자동 정리. 메인 Task.Wait 없음 → hang 없음. 미완 handoff는 버려져도 `.rosecache` 쓰기가 `File.Move` atomic rename이라 파일 시스템 일관성 유지. |
| GPU 폴백 중 ThreadGuard 위반 (이론상 발생 불가) | `FinalizeTextureOnMain`이 CPU 폴백으로 진행. Warmup은 종료까지 진행. |

---

## 검증 방법

1. **로그 확인**:
   ```
   grep "Warm-up: " Logs/editor_*.log     # 시작 로그
   grep "Warm-up complete" Logs/editor_*.log   # 종료 로그 + 총 시간
   grep "BC compress" Logs/editor_*.log   # 에셋별 압축 로그
   ```
   - "Warm-up complete: 36 assets cached (191.7s)" 같은 기존 포맷 유지.
   - 총 시간이 기존 대비 **동등 이하**여야 함 (목표: ≤ 191s).
2. **UI freeze 체감**:
   - Warmup 시작 후 ImGui 메뉴 열기, 창 이동, Project 패널 스크롤.
   - 프레임 스파이크 최대 50ms 이내.
3. **ThreadGuard 위반**:
   ```
   grep "\[ThreadGuard\]" Logs/editor_*.log
   ```
   위반 로그가 0건이어야 함.
4. **결과 동등성**:
   - Warmup 전후 `.rosecache` 파일 SHA-256 동일.

---

## 리뷰 체크리스트

- [ ] `_meshBackgroundTask`와 `_textureBackgroundTask`가 분리되어 있는가?
- [ ] 한 프레임에 두 Task가 동시에 실행되지 않는가? (현재 설계는 Mesh와 Texture 중 하나만 active.)
- [ ] `_textureBackgroundTask.IsFaulted`와 `handoff.Error` 두 케이스가 모두 처리되는가?
- [ ] `FinalizeTextureWarmupOnMain`이 throw하면 catch로 감싸 `_warmUpNext++` 보장되는가?
- [ ] 프로그레스 UI 필드(`CurrentAssetName` 등)가 정확하게 업데이트되는가?
- [ ] `IsMeshAsset == false` 경로가 TextureImporter가 아닌 에셋을 만나도 crash 없이 스킵하는가?
- [ ] Warmup 완료 후 `_meshBackgroundTask` / `_textureBackgroundTask` 모두 null로 정리되는가? (`Finish`에서)

---

## 미결 사항 (Phase 2 한정)

1. **두 Task 병렬화 여부**: 현재 설계는 "한 프레임에 하나만". 메시 Task는 CPU-bound, 텍스처 Task는 CLI/CPU-bound라 병렬 실행이 이득일 수 있지만, `.rosecache` 디렉터리 쓰기 경합과 프로그레스 UI 복잡도 증가로 **Phase 2에서는 단일 레인 유지**. 후속 개선으로 분리.
2. **Task.Run vs Task.Factory.StartNew(..., TaskCreationOptions.LongRunning)**: CLI 호출은 최대 60초라 ThreadPool 포화 위험. `LongRunning` 고려. → aca-archi 결정.

---

## 다음 단계

Phase 2 + Phase 3 한 세트로 머지 → Phase 4 검증.
