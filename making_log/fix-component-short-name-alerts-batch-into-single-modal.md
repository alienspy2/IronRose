# short name 경고 모달을 하나로 통합

## 유저 보고 내용
- 컴포넌트의 class short name 경고창이 여러 개일 때, 각각 따로 창이 뜸
- 여러 개를 한번에 모아서 하나의 창으로 보여줘야 함

## 원인
- `SceneSerializer.DeserializeComponentGeneric()`에서 short name으로 resolve된 컴포넌트마다 개별적으로 `EditorModal.EnqueueAlert()`를 호출
- `EditorModal.DrawAlertPopups()`는 큐에서 메시지를 하나씩 Dequeue하며 모달을 하나씩 띄우는 구조
- 따라서 short name 경고가 N개면 OK를 N번 눌러야 했음

## 수정 내용
- short name 경고를 즉시 EnqueueAlert하지 않고, `_shortNameWarnings` 리스트에 수집
- 씬/프리팹 로드 완료 시점에 `FlushShortNameWarnings()`를 호출하여 수집된 경고를 하나의 메시지로 합쳐서 단일 Alert로 표시
- Alert 메시지 형식: 제목 + 각 항목 목록 + 안내 문구

## 변경된 파일
- `src/IronRose.Engine/Editor/SceneSerializer.cs`
  - `_shortNameWarnings` 리스트 필드 추가
  - `FlushShortNameWarnings()` 메서드 추가 (수집된 경고를 하나의 문자열로 합쳐서 EnqueueAlert)
  - `DeserializeComponentGeneric()`: 개별 `EnqueueAlert` 호출을 `_shortNameWarnings.Add()` 로 변경
  - `LoadFromTable()`: 씬 로드 완료 시점에 `FlushShortNameWarnings()` 호출 추가
  - `LoadPrefabGameObjectsFromString()`: 프리팹 로드 완료 시점에 `FlushShortNameWarnings()` 호출 추가

## 검증
- dotnet build 성공 (오류 0개)
- 정적 분석으로 원인 특정 및 수정 (진단 로그 불필요)
