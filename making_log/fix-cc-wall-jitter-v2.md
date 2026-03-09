# CharacterController 벽 슬라이딩 jitter 수정 v2

## 유저 보고 내용
- 벽에 비빌 때 앞뒤(수평)로, 위아래(수직)로 흔들림
- 다중 평면 제약 슬라이딩 + dot product 방어 코드 적용 후에도 여전히 진동 발생
- enableOverlapRecovery = false, skinWidth = 0.08 상태

## 원인

진단 로그 분석으로 두 가지 근본 원인을 특정:

### 원인 1: 바닥 접촉에서의 isGrounded 진동 (수직 떨림)
- 홀수 프레임: 아래로 sweep → 바닥 충돌 → isGrounded=true
- 짝수 프레임: isGrounded였으므로 _verticalVelocity=-0.5f → motion.y=-0.5*dt (매우 작은 값) → sweep에서 no hit → isGrounded=false
- 다시 중력 누적 → 충돌 → grounded=true → 반복
- **매 2프레임 주기로 grounded/not-grounded가 교대하는 진동 패턴**

### 원인 2: 벽 접촉 상태가 프레임 간 보존되지 않음 (수평 떨림)
- 매 프레임 Move() 호출 시 planeCount=0으로 리셋
- 이전 프레임에서 벽과 접촉했어도, 다음 프레임에서는 "처음 보는 벽"으로 취급
- 벽 방향으로 전체 motion을 보내서 → 즉시 충돌(hit.T≈0) → safeDistance=0 → 슬라이딩 계산
- 슬라이딩 결과의 미세한 벽 방향 잔여 성분으로 position이 매 프레임 미세하게 진동

## 수정 내용

### 1. 이전 프레임 벽 접촉 법선 보존 (프레임 간 상태 유지)
- `_prevContactCount`, `_prevContactNormal0`, `_prevContactNormal1` 필드 추가
- Move() 시작 시 이전 프레임의 벽 법선으로 `motion`에서 벽 방향 성분을 사전 제거
- planeCount도 이전 법선으로 초기화하여 슬라이딩 계산의 일관성 보장

### 2. 벽(Sides) 법선만 보존, 바닥/천장 법선은 제외
- 바닥 법선(dot > 0.7)이나 천장 법선(dot < -0.7)을 보존하면, 다음 프레임에서 중력 모션이 완전히 소거되어 sweep 자체가 발생하지 않음 → isGrounded가 false가 됨 → grounded 진동 발생
- **벽 법선만 보존**하여 수직(중력) 처리에는 간섭하지 않음

### 3. 법선 보존의 지속/리셋 조건
- 현재 프레임에서 새 벽 충돌이 있으면 → 새 법선으로 갱신
- 현재 프레임에서 벽 충돌 없으면:
  - 원래 motion이 벽 방향을 향하고 있었으면 → 법선 보존이 잘 작동한 것이므로 유지
  - 원래 motion이 벽 방향을 향하지 않으면 → 유저가 방향을 바꿈 → 리셋

## 변경된 파일
- `src/IronRose.Engine/RoseEngine/CharacterController.cs`
  - `_prevContactCount`, `_prevContactNormal0`, `_prevContactNormal1` 필드 추가
  - Move() 시작부에 이전 프레임 법선 기반 모션 성분 제거 로직 추가
  - slide iteration 내에 벽(Sides) 법선 수집 로직 추가
  - slide loop 종료 후 법선 보존/리셋 로직 추가

## 검증
- dotnet build 성공 (오류 0개)
- 자동 테스트(test_commands.json)로 바닥 접촉 안정성 확인:
  - 수정 전: 매 2프레임 주기로 grounded/not-grounded 진동 (F8~F30+)
  - 수정 후: F10부터 매 프레임 안정적으로 grounded=True, 위치 고정
- 벽 슬라이딩 테스트는 자동화 한계(key_press가 1프레임만 유효)로 유저 수동 확인 필요
- **유저에게 벽 비비기 수동 테스트 요청 필요**
