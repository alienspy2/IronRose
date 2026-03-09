#!/bin/bash
# =============================================================================
# worktree-delete.sh - 특정 작업용 Git Worktree 및 브랜치 삭제
# 사용법: ./worktree-delete.sh [작업명]
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

if [ $# -lt 1 ]; then
    echo "사용법: ./worktree-delete.sh [작업명]"
    echo "현재 생성된 Task 목록:"
    ls -1 ../IronRose-worktrees/ 2>/dev/null || echo "  (없음)"
    exit 1
fi

TASK_NAME=$1
BRANCH_NAME="wt-$TASK_NAME"
BASE_DIR="$(cd .. && pwd)/IronRose-worktrees"
WORKTREE_PATH="$BASE_DIR/$TASK_NAME"

echo "=== IronRose Worktree Delete ==="
echo "  작업명: $TASK_NAME"
echo ""

# Worktree 존재 확인 및 제거
if git worktree list | grep -q "$WORKTREE_PATH"; then
    echo "  [REMOVING] Worktree: $WORKTREE_PATH"
    git worktree remove "$WORKTREE_PATH" --force
else
    echo "  [SKIP] 등록된 Worktree를 찾을 수 없습니다."
    # 디렉토리만 남아있는 경우 청소
    if [ -d "$WORKTREE_PATH" ]; then
        echo "  [RM] 물리 디렉토리 제거: $WORKTREE_PATH"
        rm -rf "$WORKTREE_PATH"
    fi
fi

# 브랜치 삭제
if git show-ref --verify --quiet "refs/heads/$BRANCH_NAME"; then
    echo "  [DELETE BRANCH] $BRANCH_NAME"
    git branch -D "$BRANCH_NAME"
else
    echo "  [SKIP] 브랜치를 찾을 수 없습니다: $BRANCH_NAME"
fi

# 공용 부모 디렉토리가 비었으면 삭제
if [ -d "$BASE_DIR" ] && [ -z "$(ls -A "$BASE_DIR" 2>/dev/null)" ]; then
    rmdir "$BASE_DIR"
    echo "  [RMDIR] $BASE_DIR (빈 디렉토리 제거)"
fi

git worktree prune

echo ""
echo "=== 정리 완료 ==="
