#!/bin/bash
# =============================================================================
# worktree-create.sh - 특정 작업을 위한 Git Worktree 동적 생성
# 사용법: ./worktree-create.sh [작업명]
# 예: ./worktree-create.sh fix-vulkan-leak
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# 인자가 없으면 타임스탬프 기반 이름 생성
TASK_NAME=${1:-"task-$(date +%Y%m%d-%H%M%S)"}
BRANCH_NAME="wt-$TASK_NAME"
WORKTREE_PATH="../IronRose-worktrees/$TASK_NAME"

# 절대 경로로 변환
BASE_DIR="$(cd .. && pwd)/IronRose-worktrees"
WORKTREE_PATH="$BASE_DIR/$TASK_NAME"

echo "=== IronRose Worktree Create ==="
echo "  작업명: $TASK_NAME"
echo "  브랜치: $BRANCH_NAME"
echo "  경로: $WORKTREE_PATH"
echo ""

# 디렉토리 생성
mkdir -p "$BASE_DIR"

# 이미 존재하는지 확인
if [ -d "$WORKTREE_PATH" ]; then
    echo "  [ERROR] 이미 해당 경로에 디렉토리가 존재합니다: $WORKTREE_PATH"
    exit 1
fi

# 브랜치가 이미 존재하는지 확인
if git show-ref --verify --quiet "refs/heads/$BRANCH_NAME"; then
    echo "  [ERROR] 이미 해당 브랜치가 존재합니다: $BRANCH_NAME"
    exit 1
fi

# worktree 추가 (-b 로 새 브랜치 생성하며 추가)
git worktree add -b "$BRANCH_NAME" "$WORKTREE_PATH" main

echo ""
echo "=== 생성 완료 ==="
echo "작업 공간으로 이동하려면:"
echo "cd $WORKTREE_PATH"
