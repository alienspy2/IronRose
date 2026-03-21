# 새 프로젝트 생성 시 default renderer가 ProjectSettings에 등록되지 않는 문제 수정

## 유저 보고 내용
- 프로젝트를 새로 생성할 때, Project Settings에 default renderer가 등록되지 않음
- Project Settings 패널에서 Active Renderer Profile이 "(None)"으로 표시됨

## 원인
두 가지 원인이 복합적으로 작용:

### 1. 템플릿의 하드코딩된 GUID
`templates/default/rose_projectSettings.toml`에 `active_profile_guid = "2ec4f1fe-2007-4cf0-80ee-d157511f0bdb"`라는 특정 GUID가 하드코딩되어 있었다. 하지만 템플릿의 `Assets/Settings/` 디렉토리에는 `.gitkeep`만 있고 실제 `.renderer` 파일이 없으므로, 새 프로젝트에서 이 GUID에 해당하는 에셋은 존재하지 않는다.

### 2. EnsureDefaultRendererProfile의 fallback GUID 갱신 누락
`EngineCore.EnsureDefaultRendererProfile()`에서 `savedGuid`(ProjectSettings에 저장된 GUID)로 프로파일 로드가 실패하면 `Default.renderer`를 경로 기반으로 fallback 로드한다. 그러나 fallback 시에도 `activeGuid`에 여전히 존재하지 않는 `savedGuid`를 사용했다:

```csharp
var activeGuid = savedGuid;
if (string.IsNullOrEmpty(activeGuid))  // savedGuid가 비어있지 않으므로 이 분기 진입 안 함
    activeGuid = _assetDatabase?.GetGuidFromPath(...);
```

결과적으로:
- `RenderSettings.activeRendererProfileGuid`에 존재하지 않는 GUID가 설정됨
- `ProjectSettings.ActiveRendererProfileGuid`도 갱신되지 않아 잘못된 GUID가 유지됨
- Project Settings 패널에서 프로파일 목록과 GUID를 매칭할 수 없어 "(None)"으로 표시됨

## 수정 내용

### EngineCore.EnsureDefaultRendererProfile() 수정
- `usedFallback` 플래그를 도입하여 savedGuid로 로드 실패 후 Default.renderer로 fallback했는지 추적
- fallback 사용 시 `activeGuid`를 `Default.renderer`의 실제 GUID로 조회하도록 변경
- fallback으로 Default 프로파일을 사용한 경우 `ProjectSettings.ActiveRendererProfileGuid`를 올바른 GUID로 갱신하고 `ProjectSettings.Save()` 호출

### 템플릿 GUID 제거
- `templates/default/rose_projectSettings.toml`의 `active_profile_guid`를 빈 문자열로 변경
- 새 프로젝트 생성 시 `EnsureDefaultRendererProfile`이 자연스럽게 Default.renderer를 생성하고 GUID를 등록하도록 함

## 변경된 파일
- `src/IronRose.Engine/EngineCore.cs` -- EnsureDefaultRendererProfile()에서 fallback 시 올바른 GUID를 조회하고 ProjectSettings에 저장하도록 수정
- `templates/default/rose_projectSettings.toml` -- 하드코딩된 존재하지 않는 GUID를 빈 문자열로 변경

## 검증
- dotnet build 성공 (에러 0개)
- 유저 확인 필요: 새 프로젝트 생성 후 Project Settings 패널에서 "Default" 렌더러가 정상 표시되는지 확인
