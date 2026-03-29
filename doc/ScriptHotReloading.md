# IronRose 스크립트 핫 리로드

## Scripts 프로젝트 구조

```
IronRose.Engine  <---- Scripts (csproj 참조, 실행 시 직접 참조하지 않음)
IronRose.Contracts <-+
                       |
                  ScriptReloadManager -- dotnet build + DLL 로드 (FileSystemWatcher 감시)
                  Standalone -- Scripts (ProjectReference)
```

- **Scripts** -- 게임 스크립트 단일 프로젝트. `dotnet build`로 컴파일되며, 에디터에서는 ScriptReloadManager가 핫 리로드 수행.
- Standalone 빌드에서는 ProjectReference로 직접 참조.

## 스크립트 핫 리로드

```
1. Scripts/*.cs 수정
2. FileSystemWatcher 감지 (0.5초 trailing edge debounce)
3. dotnet build --no-restore Scripts.csproj
4. File.ReadAllBytes(Scripts.dll) -> ALC 로드
5. MigrateEditorComponents() -> 씬 컴포넌트 마이그레이션
```

## Play Mode 동작

- Play mode 진입 시: FileSystemWatcher 중단
- Play mode 종료 시: FileSystemWatcher 재활성화, 변경 감지 시 일괄 빌드/리로드
