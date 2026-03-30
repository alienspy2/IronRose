# IronRose 프로젝트 개발 가이드라인


## 에이전트 사용 규칙

사용자가 다음 키워드를 사용하면 **반드시** 해당 에이전트(Task 도구)를 사용해야 합니다:

| 키워드 | 에이전트 | 용도 |
|--------|----------|------|
| `aca-fix` | `aca-user-feedback-and-fix` | 버그 수정, 디버깅, 기존 동작의 소규모 조정 |
| `aca-coder` | `aca-coder-csharp` | 새 코드 작성, 기능 추가, 설계 문서 기반 구현 |
| `aca-plan` | `aca-plan` | 큰 계획 수립, 요구사항 분석, 설계 문서 작성 |
| `aca-archi` | `aca-architect-csharp` | plan을 받아 coder가 구현 가능한 phase별 상세 명세서 작성 |
| `aca-code-review` | `aca-code-review` | worktree 변경점 검증, 머지 전 코드 리뷰 |

**사용 기준**:
- **큰 계획 수립, 요구사항 분석, 설계 문서 작성** → `aca-plan` (aca-plan)
- **plan을 phase별 상세 구현 명세서로 분해 (coder 투입 준비)** → `aca-archi` (aca-architect-csharp)
- **C# 코드 작성/수정** → `aca-coder` (aca-coder-csharp)
- **디버깅, 버그 수정, 기존 동작의 소규모 조정** → `aca-fix` (aca-user-feedback-and-fix)

**적극적 사용 원칙**:
- 버그 수정, 디버깅, 작은 기능 수정 작업이 필요한 경우 가능한 한 `aca-fix` 에이전트를 적극적으로 사용할 것
- 사용자가 명시적으로 키워드를 언급하지 않더라도, 버그 수정/디버깅 성격의 작업이라면 `aca-fix` 사용을 우선 고려
- `aca-coder`는 **worktree 모드**로 실행된다 (frontmatter에 `isolation: worktree` 설정됨)
- `aca-fix`는 **호출 시 worktree 여부를 선택**한다. 일반 호출 시에는 `isolation: "worktree"` 옵션을 붙이고, 리뷰 FAIL 재호출 시에는 기존 worktree에서 isolation 없이 호출한다.

**Worktree 라이프사이클**:

| 단계 | 담당 | 설명 |
|------|------|------|
| worktree 생성 | Claude Code (자동) | `isolation: worktree` frontmatter로 자동 생성 |
| 작업 + 커밋 | 서브에이전트 | 에이전트 내 "Worktree 커밋" 단계에서 수행 |
| 코드 리뷰 | `aca-code-review` | 머지 전 변경점 검증 (PASS/FAIL 판정) |
| 머지 | 메인 에이전트 | 리뷰 통과 후 반환된 브랜치를 `git merge`로 머지 |
| worktree 정리 | 메인 에이전트 | 머지 완료 후 `git worktree remove`로 정리 |

**리뷰 FAIL 시 처리**:
- 리뷰 결과가 FAIL이면, 기존 worktree를 제거하지 않고 유지한다.
- 리뷰 보고서 전문을 포함하여 `aca-fix`를 **해당 worktree 경로에서 `isolation` 없이** 재호출한다.
- 수정 완료 후 다시 `aca-code-review`로 재검증한다.
- **3회 반복 후에도 FAIL이면 유저에게 보고**하고 판단을 맡긴다.

**Worktree 머지 규칙**:
- **`git checkout --theirs` 사용 금지**. Worktree 브랜치는 이전 Wave/Phase 머지 전에 분기하므로, `--theirs`로 충돌 해결 시 이미 머지된 코드가 손실된다.
- 충돌 발생 시 **반드시 양쪽 변경을 수동으로 합칠 것**. 특히 같은 파일에 여러 Wave가 누적 추가되는 경우 주의.
- 머지 후 `dotnet build`로 빌드 확인. 누락된 코드가 없는지 `grep`으로 핵심 키워드 검증.

---

## 씬/에셋 데이터 편집 규칙

- 씬(Scene)이나 에셋(Asset) 데이터를 편집할 때는 **rose-cli 스킬을 우선 사용**할 것.
- rose-cli에 필요한 기능이 없으면 **유저에게 알리고 작업을 중단**한 뒤, rose-cli에 해당 기능을 추가하고 나서 계속할 것.
- rose-cli로 **불가능한 작업에 한해서만** 데이터 파일을 직접 편집할 것.
- rose-cli 기능 추가/수정은 **엔진/에디터 개선에 해당**한다.

---

## 엔진/에디터 우선 개선 원칙

IronRose는 개발 중인 엔진이다. 게임(Scripts) 구현 중 문제가 발생했을 때, 그 원인이 엔진이나 에디터의 미비한 기능/버그에 있다면 **게임 코드에서 우회(workaround)하지 말고 엔진/에디터 쪽을 먼저 개선**할 것.

- 엔진에 필요한 API나 기능이 없으면 → 엔진에 추가
- 에디터에 필요한 기능이 없으면 → 에디터에 추가
- 엔진/에디터 버그로 인한 문제면 → 엔진/에디터 버그를 수정

게임 코드의 workaround는 엔진 수정이 불가능하거나 비합리적인 경우에만 허용한다.

