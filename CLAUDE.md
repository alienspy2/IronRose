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

## 참조 문서

코드 작성, 디버깅, 프로젝트 구조 파악 시 아래 문서를 참조할 것:

| 문서 | 내용 |
|------|------|
| [doc/CodingGuide.md](doc/CodingGuide.md) | 코딩 스타일, 네이밍 컨벤션, Inspector 규칙, Unity와의 차이점, 디자인 테마 색상 |
| [doc/DebuggingGuide.md](doc/DebuggingGuide.md) | 로깅 전략, 디버깅 원칙, 스크린캡처 활용, 자동화 테스트 명령 |
| [doc/ScriptHotReloading.md](doc/ScriptHotReloading.md) | FrozenCode/LiveCode 구조, 핫 리로드, `/digest` 편입 워크플로우 |
| [doc/ProjectStructure.md](doc/ProjectStructure.md) | 프로젝트 디렉토리 구조 |
| [doc/RenderPipiline.md](doc/RenderPipiline.md) | 렌더링 파이프라인 |
| [doc/DesignGuide.md](doc/DesignGuide.md) | 디자인 가이드 |
| [doc/Worktree_PR_Guide.md](doc/Worktree_PR_Guide.md) | Worktree 기반 PR 가이드 |

### 매 커밋/PR 전 확인
- [ ] UTF-8 BOM 인코딩 확인 (C# 파일)
- [ ] 명명 규칙 준수
- [ ] 불필요한 파일 제외 (.gitignore 확인)
- [ ] 코드 리뷰 준비 완료
