# IronRose 스크립트 핫 리로드

## FrozenCode / LiveCode 종속성 구조

```
IronRose.Engine  ←──── FrozenCode (컴파일 타임 참조)
IronRose.Contracts ←─┘
                       ↑
                  RoseEditor ── FrozenCode (ProjectReference)
                  Standalone ── FrozenCode (ProjectReference)

IronRose.Engine  ←──── LiveCode (csproj 참조는 동일하나 실행 시 직접 참조하지 않음)
IronRose.Contracts ←─┘
                       ↑
                  LiveCodeManager ── Roslyn 런타임 컴파일 (FileSystemWatcher 감시)
```

- **FrozenCode** — `dotnet build` 시 컴파일. RoseEditor/Standalone이 `ProjectReference`로 직접 참조
- **LiveCode** — 실행 시 `LiveCodeManager`가 Roslyn으로 런타임 컴파일하여 핫 리로드. 실행 프로젝트가 직접 참조하지 않음
- 두 프로젝트 모두 동일한 종속성(`IronRose.Engine` + `IronRose.Contracts`)

## 스크립트 핫 리로드 (Phase 2)

```
1. LiveCode/*.cs 수정
2. Roslyn 런타임 컴파일
3. 즉시 로드 및 실행
```

## 스크립트 편입 (`/digest`)

엔진 실행이 종료된 후, 핫 리로드로 검증이 완료된 LiveCode 스크립트를 `/digest` 커맨드로 `FrozenCode/` 프로젝트에 편입합니다.
- LiveCode에서 테스트 완료된 `.cs` 파일을 FrozenCode 프로젝트로 이동
- LiveCode 디렉토리는 항상 실험/개발 중인 스크립트만 유지
- **중요: LiveCode ↔ FrozenCode 간 스크립트 이동은 반드시 엔진이 종료된 상태에서만 수행할 것** (실행 중 이동 시 어셈블리 불일치로 컴포넌트 참조가 깨질 수 있음)
