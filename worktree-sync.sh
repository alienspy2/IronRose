#!/bin/bash
# =============================================================================
# worktree-sync.sh - 모든 Agent Worktree를 main 최신 상태로 동기화
# 사용법: ./worktree-sync.sh [기본경로]
# 기본값: ../IronRose-worktrees/
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

BASE_DIR=${1:-"../IronRose-worktrees"}

# 절대 경로로 변환
if [ -d "$BASE_DIR" ]; then
    BASE_DIR="$(cd "$BASE_DIR" && pwd)"
fi

echo "=== IronRose Worktree Sync ==="
echo "  대상 경로: $BASE_DIR"
echo ""

if [ ! -d "$BASE_DIR" ]; then
    echo "  대상 디렉토리가 존재하지 않습니다."
    echo "  먼저 ./worktree-setup.sh 를 실행하세요."
    exit 1
fi

# origin에서 최신 변경사항 가져오기
echo "  [FETCH] origin 최신 변경사항 가져오는 중..."
git fetch origin 2>/dev/null || true
echo ""

SYNCED=0
FAILED=0

for wt_dir in "$BASE_DIR"/*; do
    if [ -d "$wt_dir" ]; then
        TASK_NAME=$(basename "$wt_dir")
        echo "  === Task: $TASK_NAME ==="

        # 작업 중인 변경사항이 있는지 확인
        if (cd "$wt_dir" && ! git diff --quiet 2>/dev/null) || \
           (cd "$wt_dir" && ! git diff --cached --quiet 2>/dev/null); then
            echo "    [WARNING] 커밋되지 않은 변경사항이 있습니다. 스킵합니다."
            echo "    수동으로 stash 하거나 커밋 후 다시 시도하세요."
            FAILED=$((FAILED + 1))
            continue
        fi

        # main으로 리베이스
        if (cd "$wt_dir" && git rebase main 2>/dev/null); then
            echo "    [OK] main 기준으로 동기화 완료"
            SYNCED=$((SYNCED + 1))
        else
            echo "    [FAIL] 리베이스 실패 - 충돌을 수동으로 해결하세요."
            (cd "$wt_dir" && git rebase --abort 2>/dev/null) || true
            FAILED=$((FAILED + 1))
        fi
        echo ""
    fi
done

echo "=== 완료: ${SYNCED}개 동기화 성공, ${FAILED}개 실패 ==="
