---
description: Git worktree 동적 관리 (작업별 생성/삭제)
---

# Git Worktree 동적 관리 워크플로우

Gemini Agent가 각 작업(Task)마다 독립된 작업 공간을 동적으로 확보하고 작업 후 정리할 수 있도록 합니다.

## 1. 새 작업 공간 생성 (Create)

새로운 기능을 구현하거나 버그를 수정하기 전, 독립된 환경을 만듭니다.

// turbo
1. 생성 스크립트 실행:
```bash
./worktree-create.sh [작업명]
```
예: `./worktree-create.sh feature-login`

- `../IronRose-worktrees/[작업명]` 경로에 작업 공간이 생깁니다.
- `wt-[작업명]` 브랜치가 자동으로 생성됩니다.

## 2. 작업 수행 (Work)

2. 생성된 경로로 이동하여 작업을 진행합니다.
```bash
cd ../IronRose-worktrees/[작업명]
```

## 3. 동기화 (Sync)

작업 도중 main의 최신 변경사항을 반영해야 할 때 사용합니다.

// turbo
3. 동기화 스크립트 실행:
```bash
./worktree-sync.sh
```

## 4. 작업 완료 및 공간 삭제 (Delete)

작업이 완료되어 main에 병합(Merge)했거나, 더 이상 필요 없는 공간을 정리합니다.

// turbo
4. 삭제 스크립트 실행:
```bash
./worktree-delete.sh [작업명]
```

## 공통 명령어

- 목록 확인: `git worktree list`
- 모든 수동 작업 완료 후 `git worktree prune` 권장
