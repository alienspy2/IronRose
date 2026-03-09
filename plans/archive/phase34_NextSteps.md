# Phase 34+ 다음 단계 제안

**작성일**: 2026-02-28
**현재 상태**: Phase 33+ 완료 (총 47,291줄 C#)

---

## A. 에디터 완성도 (기존 TODO 마무리)

### A-1. Gizmo 아이콘 렌더링
- `GizmoRendererBackend.cs`에 TODO 남아있음
- Light/Camera 등 컴포넌트 아이콘을 Scene View에 표시
- 빌보드 스프라이트 방식으로 3D 공간에 렌더링

### A-2. Custom Property Drawer
- Inspector에서 사용자 정의 타입에 대한 커스텀 에디터 드로어 등록 시스템
- Unity의 `[CustomPropertyDrawer]` 어트리뷰트에 해당

---

## B. 새로운 핵심 기능

### B-4. Animation 시스템 ⭐
- Transform/SpriteRenderer 키프레임 애니메이션
- AnimationClip, Animator, AnimatorController
- Timeline 에디터 패널 (Unity의 Animation Window에 해당)
- 커브 에디터 (Bezier 보간)

### B-5. Audio 시스템 ⭐
- 현재 오디오가 전혀 없음
- OpenAL 또는 Silk.NET.OpenAL 기반
- AudioSource, AudioClip, AudioListener 컴포넌트
- 3D 공간 오디오 (거리 감쇄, 패닝)
- WAV/OGG 에셋 임포터

### B-6. 2D Tilemap
- 2D 게임 제작에 필수적인 타일맵 시스템
- Tilemap, TilemapRenderer, Tile 컴포넌트
- 타일맵 에디터 패널 (브러시, 지우개, 채우기)
- 타일 팔레트 에셋

### B-7. Particle System
- GPU 파티클 이미터 (불, 연기, 폭발 등)
- ParticleSystem 컴포넌트
- Emission, Shape, Velocity, Color over Lifetime 모듈
- Inspector에서 파라미터 편집

---

## C. 에디터 UX/워크플로우

### C-8. Scene Template
- 빈 씬, 2D 기본 씬, 3D 기본 씬 등 템플릿 선택
- New Scene 다이얼로그에서 템플릿 목록 표시

### C-9. Build Pipeline
- 독립 실행 파일 빌드 (에디터 없이 게임만 빌드)
- 플랫폼별 빌드 설정 (Linux, Windows)
- 에셋 번들링

---

## D. 렌더링 품질

### D-10. SSAO (Screen Space Ambient Occlusion)
- Deferred 파이프라인의 G-Buffer(Normal + Depth) 활용
- 구석/틈 영역 자동 어둡게 처리
- 렌더링 품질 대폭 향상

### D-11. Anti-Aliasing
- FXAA (Fast Approximate AA) — 포스트프로세싱으로 간단 적용
- 또는 TAA (Temporal AA) — 더 높은 품질, 모션 벡터 필요

---

## 우선순위 추천

| 순위 | 항목 | 이유 |
|------|------|------|
| 1 | **B-5 Audio** 또는 **B-4 Animation** | 게임 엔진 핵심 기능, 없으면 게임 제작 불가 |
| 2 | **D-10 SSAO** | Deferred G-Buffer 활용, 렌더링 품질 향상 |
| 3 | **A-1~2 TODO 마무리** | 기존 기능의 완성도 향상 |
