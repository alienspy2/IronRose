# Sprite PPU 변경이 Reimport 후 9-slice 렌더링에 반영되지 않는 문제 수정

## 유저 보고 내용
- Inspector에서 Sprite의 `pixels_per_unit` (PPU) 값을 변경하고 Apply를 누르면, Reimport를 해도 9-slice 렌더링에 반영되지 않음
- 에디터를 완전히 껐다 켜야만 반영됨

## 원인
Reimport 시 `BuildSpriteImportResult`에서 새 Sprite 인스턴스가 생성되지만 (새 PPU 값 반영), 씬의 컴포넌트들이 이전 Sprite 인스턴스를 계속 참조하고 있었음.

구체적으로:
1. `ReplaceTextureInScene`은 `SpriteRenderer`의 기존 Sprite에서 `ReplaceTexture`로 **텍스처만** 교체 -- PPU, border, rect 등 메타데이터는 갱신되지 않음
2. `UIImage`/`UIPanel`의 Sprite 참조는 **아예 교체 로직이 없었음**
3. 에디터 재시작 시에는 씬 디시리얼라이제이션 과정에서 새 Sprite 인스턴스를 AssetDatabase에서 가져오므로 정상 동작

## 수정 내용
`ReplaceSpriteInScene` 메서드를 새로 추가하여, Reimport 시 이전 SpriteImportResult의 Sprite를 새 SpriteImportResult의 Sprite로 교체하도록 함.

- guid 기반으로 이전 Sprite와 새 Sprite를 매핑
- `SpriteRenderer._allSpriteRenderers`를 순회하여 sprite 참조 교체
- `SceneManager.AllGameObjects`를 순회하여 `UIImage`, `UIPanel`의 sprite 참조 교체
- 동기 Reimport(`Reimport`)와 비동기 Reimport(`ProcessReimport`) 두 경로 모두에 적용

## 변경된 파일
- `src/IronRose.Engine/AssetPipeline/AssetDatabase.cs`
  - `ReplaceSpriteInScene` 정적 메서드 추가 (old/new SpriteImportResult 기반 Sprite 참조 교체)
  - `Reimport()` 동기 경로: Sprite 텍스처일 때 `ReplaceSpriteInScene` 호출 추가
  - `ProcessReimport()` 비동기 경로: 동일하게 `ReplaceSpriteInScene` 호출 추가

## 검증
- 정적 분석으로 원인 특정 및 수정
- dotnet build 성공 확인
- 유저 확인 필요: PPU 변경 후 Reimport만으로 9-slice 렌더링에 즉시 반영되는지
