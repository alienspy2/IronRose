---
name: Phase 4 — 검증 / 로그 분석 / 회귀 방지
type: plan
parent: plans/warmup-texture-background-async.md
scope: 런타임 검증 (코드 변경 최소)
depends_on:
  - plans/warmup-texture-background-async-phase1-rosecache-split.md
  - plans/warmup-texture-background-async-phase2-warmup-manager.md
  - plans/warmup-texture-background-async-phase3-asset-database-api.md
status: draft
---

# Phase 4 — 검증 / 로그 분석 / 회귀 방지

> 상위: [warmup-texture-background-async.md](warmup-texture-background-async.md)
> 선행: Phase 1~3 구현 완료 및 빌드 성공.
> 목적: UI freeze가 실제로 제거되었는지, 품질/동작 회귀가 없는지 **실측**으로 확인.
> 플랫폼: **Windows / Linux 모두 검증 필요**. 코드 변경은 OS-neutral이지만 Compressonator CLI 바이너리/Veldrid 백엔드가 플랫폼별로 다르므로 두 OS에서 각각 스모크를 돌린다. 아래 시나리오는 OS별 명령을 병기한다.

---

## 검증 기준 (상위 문서 §완료 기준 재게시)

1. UI freeze 제거 — 매 프레임 메인 스레드 점유 ≤ 16ms (1024×1024 BC7 warmup 중에도 ImGui 반응).
2. 총 warmup 시간 — 동일 프로젝트에서 **≤ 191s** (기준: `Logs/editor_20260418_125322.log`).
3. 품질 동등성 — `.rosecache` 바이너리 SHA-256 동일.
4. `[ThreadGuard]` 위반 로그 0건.
5. 에러 경로 graceful — 손상 파일 / GPU 미지원 상황에서도 멈추지 않음.
6. 스모크 시나리오 6개 통과.

---

## 검증 시나리오

### S1. 기본 warmup 반응성 (기준 케이스)

**사전 준비**:
- 테스트 프로젝트:
  - Windows: `C:\git\IronRoseSimpleGameDemoProject` (로그 참조 프로젝트와 동일).
  - Linux: `/home/alienspy/git/IronRoseSimpleGameDemoProject` (primary cwd의 additional working directory).
- 캐시 클리어: 메뉴 `File → Reimport All` 또는 `RoseConfig.ForceClearCache`.

**실행**:
1. 에디터 기동.
2. Warmup 시작 직후 (Progress 오버레이 표시) ImGui 메뉴바 클릭 → `File`, `Edit`, `Project` 펼치기.
3. Project 패널 스크롤.
4. 메인 Viewport 드래그 (카메라 조작).

**기대**:
- 모든 UI 입력이 체감 100ms 이내 반응.
- "에디터 멈춤" 인상 없음.

**측정**:
- `Logs/editor_*.log`에서 warmup 기간 `[TextureImporter] Loaded` 라인 간격이 균일 (수 초 단위 점프는 CLI 호출 시간이며 메인 freeze와 무관).
- CPU 코어 분포 (메인 스레드 외 다른 코어도 활성):
  - Windows: Task Manager → Details → IronRose 프로세스 → "Analyze wait chain" 또는 Resource Monitor CPU 탭.
  - Linux: `top -H -p $(pgrep -f IronRose.Editor)` 또는 `htop` (Shift+H로 thread view).

### S2. 대용량 BC7 (1024×1024)

**사전 준비**:
- 프로젝트 `Assets` 밑에 2048×2048 테스트 PNG 파일 3개 추가. `texture_type=Color`, `quality=High` 메타 설정.
- 캐시 클리어.

**실행**: S1과 동일하게 UI 조작.

**기대**:
- `[RoseCache]     BC compress Color 2048x2048 → BC7 ...` 라인 이후 UI 동결 없음.
- CLI가 60초 타임아웃 걸려도 UI는 계속 반응.

### S3. CLI 타임아웃 유발

**사전 준비**: S2와 동일. BC7 q=1.0이 오래 걸리는 이미지 사용.

**실행**:
1. 캐시 클리어 후 기동.
2. Warmup 중 실시간 로그 관찰:
   - Linux: `tail -f Logs/editor_*.log`.
   - Windows (PowerShell): `Get-Content -Wait Logs/editor_*.log`.
   - Windows (Git Bash): `tail -f Logs/editor_*.log`.

**기대**:
- `Compressonator CLI timed out (60s)` 로그 출현 시에도 ImGui는 반응.
- 해당 에셋의 `BC compress done via GPU ...` 라인이 수 십~ 수 백 ms 범위.

### S4. CLI 없는 환경 (CPU 폴백)

**사전 준비** (OS별 이름변경):
- Windows: `externalTools/compressonatorcli/windows/compressonatorcli.exe` → `compressonatorcli.exe.bak`.
- Linux: `externalTools/compressonatorcli/linux/compressonatorcli-bin` → `compressonatorcli-bin.bak`.
- 캐시 클리어.

**실행**: 에디터 기동.

**기대**:
- 로그: `[RoseCache] Compressonator CLI not found, will use fallback compressors`.
- 이후 모든 BC 압축이 `BC compress done via GPU ...` 또는 `CPU ...`.
- UI 반응성 유지.

**사후 정리**: `.bak`를 원상 복구 (양 플랫폼 공통).

### S5. Reimport All

**사전 준비**: 에디터 이미 기동 + warmup 완료 상태.

**실행**:
1. 메뉴 `File → Reimport All`.
2. Confirmation 팝업 → OK.
3. 에디터 재시작 자동 수행 → 캐시 클리어 + warmup 재실행.

**기대**:
- S1과 동일한 반응성.

### S6. 플레이모드 진입/종료

**사전 준비**: Warmup 완료된 상태.

**실행**:
1. 플레이 시작 → 게임 동작.
2. 플레이 중 Project 패널에서 텍스처 하나 재임포트 (Inspector Reimport 버튼).
3. 플레이 종료.

**기대**:
- 플레이 중 reimport의 `StoreCacheOrDefer` → `_pendingPrecompressedTextures` enqueue.
- 플레이 종료 후 `FlushPendingCacheOps`가 drain하는 로그:
  `[AssetDatabase] Flushed N deferred cache operations after Play stop`.
- crash / race 없음.

---

## 로그 분석 체크리스트

Warmup 완료 후 `Logs/editor_*.log`를 열어 다음을 확인.

### C1. Warmup 소요 시간

```
grep "Warm-up complete" Logs/editor_*.log
```

- 형식: `[Engine] Warm-up complete: N assets cached (Ts)`.
- 기존 로그(`Logs/editor_20260418_125322.log:399` → 191.7s) 대비 동등 이하.

### C2. 에셋별 CLI/GPU/CPU 분포

```
grep "Fallback path:" Logs/editor_*.log | awk -F'[,:]' '{print $2, $4, $6, $8}' | sort | uniq -c
```

- 기존 분포 대비 동등 (CLI/GPU 폴백 비율이 크게 바뀌지 않아야 함).

### C3. ThreadGuard 위반

```
grep "\[ThreadGuard\]" Logs/editor_*.log
```

- **0건** 이어야 함. 1건이라도 있으면 해당 context 문자열로 원인 추적.

### C4. `.rosecache` 동등성

Warmup 전 (OS별):
- Linux / Git Bash: `sha256sum Library/RoseCache/*.rosecache > /tmp/before.sha`
- Windows PowerShell: `Get-FileHash Library/RoseCache/*.rosecache -Algorithm SHA256 | Out-File $env:TEMP\before.sha`

Phase 4 적용 후 재 warmup, diff:
- Linux / Git Bash:
  ```
  sha256sum Library/RoseCache/*.rosecache > /tmp/after.sha
  diff /tmp/before.sha /tmp/after.sha
  ```
- Windows PowerShell:
  ```
  Get-FileHash Library/RoseCache/*.rosecache -Algorithm SHA256 | Out-File $env:TEMP\after.sha
  Compare-Object (Get-Content $env:TEMP\before.sha) (Get-Content $env:TEMP\after.sha)
  ```

- diff 출력 없어야 함. 예외: CLI가 Mode 탐색 상 비결정적일 가능성이 있으면 sample 에셋 수 개만 비교하거나 무시.

**주의**: Compressonator CLI는 동일 입력에 대해 결정적(비확률적). GPU 경로도 compute shader가 결정적 구조라면 동일. CPU BCnEncoder.NET도 결정적. 따라서 바이트 단위 동일이 기대됨. **플랫폼 간 `.rosecache` 호환성 주의**: Windows에서 생성된 캐시와 Linux에서 생성된 캐시는 CLI 바이너리가 다르므로 바이트 단위로 같다는 보장은 없다. 동등성 검증은 **동일 OS 내에서 Phase 적용 전후 비교**로 수행.

### C5. FSW / Metadata race 없음

```
grep -i "race\|conflict\|dirty\|invalid" Logs/editor_*.log
```

- 기존 로그 대비 새 경고 없음.

---

## 성능 프로파일링 (선택)

### 프로파일링 스냅샷 (OS별)

**Windows — PerfView**:
1. Warmup 시작 직전 PerfView 시작.
2. Warmup 완료까지 기록 (약 3분).
3. 정지 후 분석:
   - 메인 스레드 CPU time — warmup 기간 동안 GPU 폴백 시점 외에는 수 ms 수준.
   - `Task.Run` 백그라운드 스레드 — CLI 프로세스 WaitForExit이 주 소요.
   - 메인 스레드 Wait 시간 — freeze가 없다면 수 ms 이내.

**Linux — `dotnet-trace` / `perf`**:
1. `dotnet-trace collect -p $(pgrep -f IronRose.Editor) --duration 00:03:00 -o warmup.nettrace` 또는
   `perf record -F 99 -g -p $(pgrep -f IronRose.Editor) -- sleep 180`.
2. 분석:
   - `dotnet-trace`: `dotnet-trace convert warmup.nettrace --format speedscope` → speedscope.app에서 flamegraph 확인.
   - `perf`: `perf report` 또는 `perf script | stackcollapse-perf.pl | flamegraph.pl > warmup.svg`.
3. 확인 포인트는 Windows와 동일 (메인 스레드 점유 / Task 스레드 CLI WaitForExit).

### 프레임 시간 로깅 (옵션)

`EngineCore.Update` 마지막에 `Stopwatch`로 프레임 소요 측정, `> 100ms`면 warning log 추가하는 임시 패치로 확인. 스모크 통과 후 제거.

---

## 회귀 방지 — 영속적 검증 장치 (선택)

Phase 4 자체는 런타임 검증이지만, 미래 회귀 방지를 위해 추가할 수 있는 것:

### 선택 1. `ThreadGuard` 추가 삽입
- `RoseCache.StoreTexturePrecompressed`, `RoseCache.FinalizeTextureOnMain` (Phase 1에서 이미 완료).
- `AssetDatabase.FinalizeTextureWarmupOnMain` (Phase 3에서 이미 완료).

### 선택 2. 통합 스모크 체크리스트 (문서만)
- `plans/archive/warmup-texture-async-smoke-checklist.md` 같은 체크리스트 작성.
- Phase B/C/D의 기존 스모크 체크리스트와 합병 가능.

### 선택 3. warmup 벤치마크 CLI 명령
- `CliCommandDispatcher`에 `benchmark.warmup` 명령을 추가하여 캐시 클리어 + warmup 실행 + 총 시간/최대 프레임 보고.
- Phase 4 범위 밖. 별도 후속 작업으로 분리.

---

## 실패 처리 (검증 중 실패 시)

| 실패 유형 | 대응 |
|-----------|------|
| UI freeze 여전히 발생 | 메인 프레임 Stopwatch로 어떤 호출이 길게 블록하는지 파악. 유력 후보: `FinalizeTextureOnMain`의 GPU 경로가 1024² 큰 텍스처에서 느림. GPU 경로 자체를 프레임 분할 (한 프레임에 mip 하나씩)로 추가 개선 — 후속 Phase. |
| warmup 총 시간 증가 | Task.Run 스케줄링 오버헤드가 원인이면 LongRunning 플래그 고려. 혹은 mesh+texture 병렬 레인 추가 (Phase 2 미결 #1). |
| SHA-256 불일치 | 에셋 수 개를 뽑아 diff. 포맷 변경(BC1→BC3 등) 폴백 타이밍이 달라졌는지 확인. 로그의 `Fallback path:` 비교. |
| ThreadGuard 위반 발생 | context 문자열로 위치 특정. 해당 지점 호출 경로 추적 후 수정. |
| 플레이모드 deferred flush 누락 | `_pendingPrecompressedTextures` enqueue / drain 로그 추가하여 경로 확인. |

실패 시 **`aca-fix` 에이전트**로 해당 worktree 재호출하여 수정. 3회 반복 후에도 실패 시 유저에게 보고 (CLAUDE.md 규칙).

---

## 완료 처리

Phase 4 검증 통과 시:
1. worktree 커밋 정리 (Phase 1~4가 한 worktree라면 단일 커밋 또는 Phase별 커밋).
2. `aca-code-review`로 최종 리뷰.
3. 메인 브랜치에 머지.
4. `plans/archive/` 에 본 문서와 Phase 1~4 문서를 이동 (완료 표시).
5. 관련 `making_log/` 항목 작성 (warmup-texture-background-async 최종 로그).

---

## 리뷰 체크리스트

- [ ] 스모크 S1~S6 모두 통과 기록 — **Windows / Linux 양쪽**?
- [ ] 로그 C1~C5 모두 기준 충족 — **Windows / Linux 양쪽**?
- [ ] `.rosecache` SHA-256 동등 확인 (동일 OS 내 전후 비교)?
- [ ] `[ThreadGuard]` 위반 0건 — **Windows / Linux 양쪽**?
- [ ] 품질 저하 없음 (시각적 비교 — 샘플 5~10개 에셋 Inspector 프리뷰 대조)?
- [ ] 총 warmup 시간 ≤ 191s (Windows 기준) / Linux 별도 기준 측정?
- [ ] Linux `compressonatorcli-bin` 실행권한(`chmod +x`) 및 `pkglibs` 로드 정상 동작?
