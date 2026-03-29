# Rigidbody allowSleep 프로퍼티 추가 및 기본값 false로 설정

## 유저 보고 내용
- 큐브(Cube) 리지드바디의 sleep 기능을 아예 비활성화해야 함
- sleep이 발생하지 않도록 설정 필요

## 원인
- BepuPhysics는 body가 안정 상태(velocity가 threshold 이하)에 도달하면 자동으로 sleep 상태로 전환함
- sleep 상태의 body는 외부에서 명시적으로 깨우지 않으면 시뮬레이션에 참여하지 않음
- 기존 Rigidbody 컴포넌트에는 sleep 제어 옵션이 없어서 모든 dynamic body가 기본적으로 sleep 가능했음

## 수정 내용
1. **PhysicsWorld3D.SetBodyAllowSleep(BodyHandle, bool)** 메서드 추가
   - BepuPhysics의 `BodyActivity.SleepThreshold`를 음수(-1)로 설정하면 자동 sleep이 불가능해짐
   - `allowSleep=true`로 복원 시 기본 threshold(0.01)로 복구

2. **Rigidbody.allowSleep** 프로퍼티 추가 (기본값: `false`)
   - 런타임 변경 가능 (setter에서 즉시 physics world에 반영)
   - `RegisterWithPhysics()`에서 body 등록 후 `allowSleep`이 `false`이면 sleep threshold를 음수로 설정
   - Inspector에서 자동으로 표시됨 (리플렉션 기반 직렬화)

## 변경된 파일
- `src/IronRose.Physics/PhysicsWorld3D.cs` -- SetBodyAllowSleep 메서드 추가
- `src/IronRose.Engine/RoseEngine/Rigidbody.cs` -- allowSleep 프로퍼티 추가 (기본값 false), RegisterWithPhysics에서 sleep 비활성화 적용, 헤더 주석 업데이트

## 검증
- dotnet build 성공 (0 Error)
- 실행 테스트는 유저 확인 필요: 에디터에서 Rigidbody가 있는 오브젝트가 sleep 상태에 빠지지 않는지 확인
