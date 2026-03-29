# IronRose 프로젝트 개발 가이드라인


## 에이전트 사용 규칙

사용자가 다음 키워드를 사용하면 **반드시** 해당 에이전트(Task 도구)를 사용해야 합니다:

| 키워드 | 에이전트 | 용도 |
|--------|----------|------|
| `aca-fix` | `aca-user-feedback-and-fix` | 버그 수정, 디버깅, 작은 기능 수정 |
| `aca-plan` | `aca-plan` | 세부 기능 설계, Phase 계획 문서 작성 |
| `aca-archi` | `aca-architect-csharp` | 큰 아키텍처 설계, Phase별 상세 구현 명세서 작성 |

**사용 기준**:
- **큰 계획/아키텍처 설계** → `aca-archi` (aca-architect-csharp)
- **세부 기능 계획/설계 문서** → `aca-plan` (aca-plan)
- **디버깅, 버그 수정, 작은 기능 수정** → `aca-fix` (aca-user-feedback-and-fix)

**적극적 사용 원칙**:
- 버그 수정, 디버깅, 작은 기능 수정 작업이 필요한 경우 가능한 한 `aca-fix` 에이전트를 적극적으로 사용할 것
- 사용자가 명시적으로 키워드를 언급하지 않더라도, 버그 수정/디버깅 성격의 작업이라면 `aca-fix` 사용을 우선 고려

---

## 에디터 실행

```bash
dotnet run --project src/IronRose.RoseEditor
```

**주의**: 빌드를 다시 하고 에디터를 재실행해야 할 경우, **이미 실행 중인 에디터를 반드시 먼저 종료**할 것. 빌드를 다시 할 필요가 없으면 이미 켜져 있는 에디터를 그대로 사용한다. 에디터가 실행 중인 상태에서 파일을 수정하는 것은 허용된다.

---

## 씬/에셋 데이터 편집 규칙

- 씬(Scene)이나 에셋(Asset) 데이터를 편집할 때는 **rose-cli 스킬을 우선 사용**할 것.
- rose-cli에 필요한 기능이 없으면 **유저에게 알리고 작업을 중단**한 뒤, rose-cli에 해당 기능을 추가하고 나서 계속할 것.
- rose-cli로 **불가능한 작업에 한해서만** 데이터 파일을 직접 편집할 것.

---

## 엔진/에디터 우선 개선 원칙

IronRose는 개발 중인 엔진이다. 게임(LiveCode) 구현 중 문제가 발생했을 때, 그 원인이 엔진이나 에디터의 미비한 기능/버그에 있다면 **게임 코드에서 우회(workaround)하지 말고 엔진/에디터 쪽을 먼저 개선**할 것.

- 엔진에 필요한 API나 기능이 없으면 → 엔진에 추가
- 에디터에 필요한 기능이 없으면 → 에디터에 추가
- 엔진/에디터 버그로 인한 문제면 → 엔진/에디터 버그를 수정

게임 코드의 workaround는 엔진 수정이 불가능하거나 비합리적인 경우에만 허용한다.

---

## 필수 규칙

- **코드 작성/수정 전** 반드시 `doc/CodingGuide.md`를 Read 할 것.
- **디버깅/버그 수정 전** 반드시 `doc/DebuggingGuide.md`를 Read 할 것.
- 디버깅 로그는 반드시 `Debug.Log`(`using RoseEngine`)를 사용. `File.WriteAllText`, `File.AppendAllText` 등으로 별도 로그 파일 생성 금지.
- 서브에이전트(aca-fix 등)에게 작업 위임 시에도 위 규칙을 프롬프트에 명시할 것.
- **코드 수정 후 반드시 `dotnet build`로 빌드 확인.** 빌드 에러는 반드시 수정. 워닝도 가능하면 수정할 것.

---

## Worktree 머지 규칙

- **`git checkout --theirs` 사용 금지**. Worktree 브랜치는 이전 Wave/Phase 머지 전에 분기하므로, `--theirs`로 충돌 해결 시 이미 머지된 코드가 손실된다.
- 충돌 발생 시 **반드시 양쪽 변경을 수동으로 합칠 것**. 특히 같은 파일에 여러 Wave가 누적 추가되는 경우 주의.
- 머지 후 `dotnet build`로 빌드 확인. 누락된 코드가 없는지 `grep`으로 핵심 키워드 검증.

---

## 참조 문서

작업 유형에 따라 해당 문서를 **반드시 먼저 Read한 뒤** 작업을 시작할 것:

| 문서 | 언제 읽는가 |
|------|------------|
| [doc/CodingGuide.md](doc/CodingGuide.md) | 코드 작성/수정 시 |
| [doc/DebuggingGuide.md](doc/DebuggingGuide.md) | 디버깅/버그 수정 시 |
| [doc/DesignGuide.md](doc/DesignGuide.md) | UI/에디터 디자인 작업 시 |
| [doc/ProjectStructure.md](doc/ProjectStructure.md) | 프로젝트 구조 파악 필요 시 |
| [doc/ScriptHotReloading.md](doc/ScriptHotReloading.md) | 스크립트/핫 리로드 관련 작업 시 |
| [doc/RenderPipiline.md](doc/RenderPipiline.md) | 렌더링 관련 작업 시 |
| [doc/Worktree_PR_Guide.md](doc/Worktree_PR_Guide.md) | PR 생성 시 |

### 매 커밋/PR 전 확인
- [ ] UTF-8 BOM 인코딩 확인 (C# 파일)
- [ ] 명명 규칙 준수
- [ ] 불필요한 파일 제외 (.gitignore 확인)
- [ ] 코드 리뷰 준비 완료
