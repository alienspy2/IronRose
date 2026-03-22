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
