# RoseCache Store를 플레이모드 중 보류하고 종료 후 일괄 처리

## 유저 보고 내용
- `AssetDatabase`에서 `_roseCache.StoreTexture()`, `_roseCache.StoreMesh()`가 7곳에서 호출됨
- 플레이모드 중에도 이 호출이 실행되어 cache 관련 문제가 발생할 수 있음
- 기존 `LiveCodeManager`의 핫 리로드 보류 패턴과 동일하게, 플레이모드 중 캐시 저장을 보류하고 Stop 후 일괄 수행하도록 요청

## 원인
- `_roseCache.StoreTexture()`/`_roseCache.StoreMesh()` 호출이 플레이모드 상태를 고려하지 않고 무조건 즉시 실행됨
- EnsureDiskCached, StoreDiskCache, Reimport, 백그라운드 Reimport 등 여러 경로에서 호출됨

## 수정 내용

### AssetDatabase.cs
1. `using IronRose.Engine.Editor;` 추가 (EditorPlayMode 참조)
2. `using System.Collections.Concurrent;` 추가 (ConcurrentQueue 사용)
3. `ConcurrentQueue` 기반 보류 큐 2개 추가:
   - `_pendingCacheTextures`: 텍스처 캐시 보류 큐
   - `_pendingCacheMeshes`: 메시 캐시 보류 큐
4. `StoreCacheOrDefer(path, tex, meta)` 래퍼 메서드 추가 (Texture2D 오버로드)
5. `StoreCacheOrDefer(path, result, meta)` 래퍼 메서드 추가 (MeshImportResult 오버로드)
6. `FlushPendingCacheOps()` public 메서드 추가: 보류 중인 캐시 저장을 일괄 수행
7. 기존 7곳의 `_roseCache.StoreTexture(...)`/`_roseCache.StoreMesh(...)` 호출을 `StoreCacheOrDefer(...)` 로 교체:
   - EnsureDiskCached: 2곳 (Mesh, Texture)
   - StoreDiskCache: 2곳 (Mesh, Texture)
   - Reimport (동기): 2곳 (Mesh, Texture)
   - Reimport (백그라운드 Task.Run): 1곳 (Mesh, Texture 각 1개 = 2곳... 실제로 여기서 총 7개)

### EngineCore.cs
- `InitAssets()` 마지막에 `EditorPlayMode.OnAfterStopPlayMode` 콜백으로 `AssetDatabase.FlushPendingCacheOps()` 등록
- 기존 `InitLiveCode()`의 `FlushPendingReload()` 등록 패턴과 동일

## 설계 결정
- `Queue` 대신 `ConcurrentQueue` 사용: 백그라운드 Reimport의 `Task.Run` 내부에서도 `StoreCacheOrDefer`가 호출되므로, 스레드 안전성을 보장하기 위해 concurrent collection 사용
- `StoreCacheOrDefer`는 `EditorPlayMode.IsInPlaySession` 체크 후 분기: 플레이모드 중이면 큐에 보류, 아니면 즉시 캐시 저장

## 변경된 파일
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs` -- ConcurrentQueue 보류 큐, StoreCacheOrDefer 래퍼, FlushPendingCacheOps 추가, 7곳 교체
- `src/IronRose.Engine/EngineCore.cs` -- InitAssets()에 OnAfterStopPlayMode 콜백 등록

## 동작 흐름
1. 에디터 모드 (플레이 아님): `StoreCacheOrDefer` -> `IsInPlaySession == false` -> 즉시 `_roseCache.Store*()` 실행 (기존과 동일)
2. 플레이모드 진입
3. 에셋 임포트/리임포트 발생 -> `StoreCacheOrDefer` -> `IsInPlaySession == true` -> 큐에 보류
4. 플레이모드 중 추가 에셋 변경이 있어도 큐에 누적
5. play.stop -> `StopPlayMode()` -> 씬 복원 -> `OnAfterStopPlayMode` -> `FlushPendingCacheOps()` -> 보류된 캐시 저장 일괄 수행

## 검증
- 빌드 성공 확인 (0 Error, 기존 경고만 존재)
- 실제 플레이모드 진입/에셋 변경/Stop 시나리오는 유저 확인 필요
