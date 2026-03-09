// ------------------------------------------------------------
// @file    CharacterController.cs
// @brief   Unity 호환 CharacterController 컴포넌트. Collider를 상속하며 캡슐 형상의
//          키네마틱 캐릭터 이동을 제공한다. Move()는 sweep 기반 충돌 감지 + 슬라이딩 +
//          슬로프 제한 + 계단 오르기를 수행한다.
// @deps    Collider, CollisionFlags, ControllerColliderHit, Vector3, Mathf, Gizmos, Matrix4x4,
//          MonoBehaviour, Physics (gravity), Time (deltaTime),
//          IronRose.Engine.PhysicsManager, IronRose.Physics.SweepHit,
//          BepuPhysics.StaticHandle, BepuPhysics.Collidables.CollidableReference
// @exports
//   class CharacterController : Collider
//     height: float (default 2.0f)                 — 캡슐 전체 높이
//     radius: float (default 0.5f)                 — 캡슐 반지름
//     slopeLimit: float (default 45f)              — 등반 가능 최대 경사각 (도)
//     stepOffset: float (default 0.3f)             — 자동 계단 오르기 높이
//     skinWidth: float (default 0.08f)             — 충돌 스킨 두께
//     minMoveDistance: float (default 0.001f)      — 무시할 최소 이동 거리
//     detectCollisions: bool (default true)        — 충돌 감지 활성화
//     enableOverlapRecovery: bool (default true)   — 겹침 보정 활성화
//     isGrounded: bool (readonly)                  — 바닥 접촉 여부
//     collisionFlags: CollisionFlags (readonly)    — 마지막 Move의 충돌 방향
//     velocity: Vector3 (readonly)                 — 마지막 Move의 속도
//     Move(Vector3 motion): CollisionFlags         — sweep 기반 충돌 감지 이동 (중력 미적용, overlap recovery 포함)
//     SimpleMove(Vector3 speed): bool              — 중력 자동 적용 이동 (XZ speed + 내부 중력 누적)
//     SyncStaticPose(PhysicsManager): void         — PhysicsManager에서 호출하는 pose 동기화 (internal)
// @note    Move()는 최대 3회 slide iteration으로 벽/바닥/천장 슬라이딩 처리.
//          slopeLimit 초과 경사면에서는 수평 슬라이딩 적용.
//          isGrounded && Sides 충돌 시 stepOffset 이내 높이차는 자동 계단 오르기.
//          RegisterAsStatic에서 SetStaticUserData로 자기 자신 등록.
//          enableOverlapRecovery=true일 때 Move() 시작 전 OverlapCapsule로 겹침 보정 수행.
//          SimpleMove()는 Physics.gravity로 수직 속도 누적, isGrounded 시 -0.5f로 리셋.
// ------------------------------------------------------------
using System;
using System.Collections.Generic;
using BepuPhysics;
using BepuPhysics.Collidables;
using IronRose.Physics;
using SysVector3 = System.Numerics.Vector3;
using SysQuaternion = System.Numerics.Quaternion;

namespace RoseEngine
{
    public class CharacterController : Collider
    {
        // ── Unity 호환 프로퍼티 ──

        public float height { get; set; } = 2.0f;
        public float radius { get; set; } = 0.5f;
        // center는 Collider에서 상속

        public float slopeLimit { get; set; } = 45f;
        public float stepOffset { get; set; } = 0.3f;
        public float skinWidth { get; set; } = 0.08f;
        public float minMoveDistance { get; set; } = 0.001f;

        public bool detectCollisions { get; set; } = true;
        public bool enableOverlapRecovery { get; set; } = false;

        /// <summary>Dynamic body 충돌 시 적용할 밀어내기 힘 배율. 0이면 밀지 않음.</summary>
        public float pushPower { get; set; } = 2.0f;

        // ── 읽기 전용 상태 ──

        public bool isGrounded { get; private set; }
        public CollisionFlags collisionFlags { get; private set; }
        public Vector3 velocity { get; private set; }

        // ── 내부 상태 ──

        private float _simpleMoveVerticalSpeed;
        internal StaticHandle? _kinematicHandle;

        private const int MAX_SLIDE_ITERATIONS = 3;
        private const float GROUND_NORMAL_THRESHOLD = 0.7f;
        private const float CEILING_NORMAL_THRESHOLD = -0.7f;

        // 이전 프레임의 접촉 법선을 보존하여 다음 프레임에서 즉시 슬라이딩에 반영
        private int _prevContactCount;
        private Vector3 _prevContactNormal0;
        private Vector3 _prevContactNormal1;

        // ── 타입 변환 헬퍼 ──

        private static SysVector3 ToSys(Vector3 v) => new(v.x, v.y, v.z);
        private static Vector3 FromSys(SysVector3 v) => new(v.X, v.Y, v.Z);
        private static SysQuaternion ToSysQ(Quaternion q) => new(q.x, q.y, q.z, q.w);

        // ── API 메서드 ──

        /// <summary>충돌 감지하며 motion만큼 이동. 중력 미적용.</summary>
        public CollisionFlags Move(Vector3 motion)
        {
            // 1. 최소 이동 거리 미만이면 조기 리턴
            float motionMag = motion.magnitude;
            if (motionMag < minMoveDistance)
                return CollisionFlags.None;

            // 2. 상태 초기화
            collisionFlags = CollisionFlags.None;
            isGrounded = false;

            // PhysicsManager 접근
            var mgr = IronRose.Engine.PhysicsManager.Instance;
            if (mgr == null) return CollisionFlags.None;

            // 2.5. Overlap Recovery: 겹침 보정
            if (enableOverlapRecovery)
                PerformOverlapRecovery(mgr);

            // 3. 스케일 계산
            var s = transform.lossyScale;
            float radiusScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
            float scaleY = Mathf.Abs(s.y);
            float scaledRadius = radius * radiusScale;
            float sweepRadius = Mathf.Max(0.01f, scaledRadius - skinWidth);
            float halfLength = Mathf.Max(0.01f, height * scaleY - 2f * scaledRadius) / 2f;

            // 4. 현재 캡슐 중심 위치 (월드 좌표)
            Vector3 startPosition = transform.TransformPoint(center);
            Vector3 position = startPosition;
            SysQuaternion orientation = ToSysQ(transform.rotation);

            // 4.5. 같은 GO의 모든 Collider static handle을 수집 (자기 자신 포함)
            StaticHandle[] excludeHandles = CollectSameGameObjectStaticHandles();

            // 5. Slide iteration — 다중 평면 제약 처리
            Vector3 remainingMotion = motion;
            int planeCount = 0;
            Vector3 plane0Normal = Vector3.zero;
            Vector3 plane1Normal = Vector3.zero;

            // 이전 프레임 접촉 법선으로 planeCount 초기화:
            // 벽에 접촉한 채 다음 프레임이 시작되면, 첫 iteration에서
            // "처음 보는 벽"으로 취급되어 한 번 벽 안쪽으로 이동 후 충돌하는
            // 진동 패턴을 방지한다.
            if (_prevContactCount >= 1)
            {
                float dotWithPrev0 = Vector3.Dot(remainingMotion, _prevContactNormal0);
                if (dotWithPrev0 < -0.001f)
                {
                    // 이전 벽 법선 안쪽으로 향하는 모션 → 해당 성분 제거
                    remainingMotion -= _prevContactNormal0 * dotWithPrev0;
                    plane0Normal = _prevContactNormal0;
                    planeCount = 1;
                }
            }
            if (_prevContactCount >= 2 && planeCount >= 1)
            {
                float dotWithPrev1 = Vector3.Dot(remainingMotion, _prevContactNormal1);
                if (dotWithPrev1 < -0.001f)
                {
                    remainingMotion -= _prevContactNormal1 * dotWithPrev1;
                    plane1Normal = _prevContactNormal1;
                    planeCount = 2;
                }
            }

            // 현재 프레임에서 수집할 접촉 법선 (프레임 끝에서 _prevContact*에 저장)
            int curContactCount = 0;
            Vector3 curContactNormal0 = Vector3.zero;
            Vector3 curContactNormal1 = Vector3.zero;

            for (int i = 0; i < MAX_SLIDE_ITERATIONS; i++)
            {
                float remainingDist = remainingMotion.magnitude;
                if (remainingDist < 0.0001f)
                    break;

                Vector3 direction = remainingMotion / remainingDist;

                if (!detectCollisions)
                {
                    position += remainingMotion;
                    break;
                }

                // Sweep (같은 GO의 모든 collider를 제외)
                bool hasHit = mgr.World3D.SweepCapsule(
                    ToSys(position),
                    orientation,
                    sweepRadius,
                    halfLength,
                    ToSys(direction),
                    remainingDist,
                    out SweepHit hit,
                    excludeHandles);

                if (!hasHit)
                {
                    // 충돌 없음 — 전체 이동
                    position += remainingMotion;
                    break;
                }

                // 충돌 있음 — skinWidth만큼 뒤에서 멈춤
                float hitDistance = hit.T * remainingDist;
                float safeDistance = Mathf.Max(0f, hitDistance - skinWidth);
                position += direction * safeDistance;

                // 법선 변환
                Vector3 hitNormal = FromSys(hit.Normal);
                Vector3 hitPoint = FromSys(hit.HitPosition);
                float normalDotUp = Vector3.Dot(hitNormal, Vector3.up);

                // 접촉 법선 수집 (프레임 끝에서 보존용)
                // 벽(Sides) 법선만 수집 — 바닥/천장 법선을 보존하면 다음 프레임에서
                // 중력 모션이 소거되어 isGrounded 판정이 교대로 진동하는 문제 발생
                // Dynamic body 법선은 수집하지 않음 — dynamic body는 이동하므로
                // 이전 프레임 법선이 다음 프레임에서 유효하지 않음 (보이지 않는 벽 버그 방지)
                bool isDynamicBody = hit.Collidable.Mobility != CollidableMobility.Static;
                bool isSidesNormal = normalDotUp <= GROUND_NORMAL_THRESHOLD
                                  && normalDotUp >= CEILING_NORMAL_THRESHOLD;
                if (isSidesNormal && !isDynamicBody && curContactCount == 0)
                {
                    curContactNormal0 = hitNormal;
                    curContactCount = 1;
                }
                else if (isSidesNormal && !isDynamicBody && curContactCount == 1 && Vector3.Dot(hitNormal, curContactNormal0) < 0.99f)
                {
                    curContactNormal1 = hitNormal;
                    curContactCount = 2;
                }

                // CollisionFlags 갱신
                if (normalDotUp > GROUND_NORMAL_THRESHOLD)
                {
                    collisionFlags |= CollisionFlags.Below;
                    isGrounded = true;
                }
                else if (normalDotUp < CEILING_NORMAL_THRESHOLD)
                {
                    collisionFlags |= CollisionFlags.Above;
                }
                else
                {
                    collisionFlags |= CollisionFlags.Sides;
                }

                // 충돌 대상이 dynamic body이면 sleep에서 깨우고 밀어냄
                if (hit.Collidable.Mobility != CollidableMobility.Static)
                {
                    var bodyHandle = hit.Collidable.BodyHandle;
                    mgr.World3D.WakeBody(bodyHandle);

                    // 이동 방향으로 impulse 적용 (수평 성분만)
                    if (pushPower > 0f)
                    {
                        Vector3 pushDir = new Vector3(direction.x, 0f, direction.z);
                        float pushDirMag = pushDir.magnitude;
                        if (pushDirMag > 0.001f)
                        {
                            pushDir /= pushDirMag;
                            float speed = motion.magnitude / Mathf.Max(Time.deltaTime, 0.001f);
                            SysVector3 impulse = ToSys(pushDir) * speed * pushPower * Time.deltaTime;
                            mgr.World3D.ApplyLinearImpulse(bodyHandle, impulse);
                        }
                    }
                }

                // OnControllerColliderHit 콜백 발송
                object? userData = mgr.World3D.GetUserData(hit.Collidable);
                FireControllerColliderHit(hitPoint, hitNormal, direction, remainingDist, userData);

                // 이동 후 남은 모션 계산 — 실제로 position이 이동한 safeDistance 기준으로 차감
                Vector3 leftoverMotion = remainingMotion - direction * safeDistance;

                // Step 처리: Sides 충돌 && isGrounded && 충돌점이 stepOffset 이내
                bool stepHandled = false;
                if ((collisionFlags & CollisionFlags.Sides) != 0 && isGrounded)
                {
                    float capsuleBottom = position.y - (height * scaleY / 2f);
                    float hitHeight = hitPoint.y - capsuleBottom;
                    if (hitHeight >= 0f && hitHeight <= stepOffset)
                    {
                        stepHandled = TryStepUp(mgr, ref position, orientation,
                            sweepRadius, halfLength, direction, remainingDist - safeDistance,
                            scaleY);
                    }
                }

                if (!stepHandled)
                {
                    // Slope 처리: 바닥 충돌 && 경사각 > slopeLimit → 미끄러짐
                    if ((collisionFlags & CollisionFlags.Below) != 0)
                    {
                        float slopeAngle = Mathf.Acos(Mathf.Clamp(normalDotUp, -1f, 1f)) * Mathf.Rad2Deg;
                        if (slopeAngle > slopeLimit)
                        {
                            Vector3 slideDir = hitNormal - Vector3.up * normalDotUp;
                            if (slideDir.sqrMagnitude > 0.0001f)
                            {
                                slideDir = slideDir.normalized;
                                remainingMotion = slideDir * leftoverMotion.magnitude;
                                planeCount = 0; // slope slide는 평면 제약 리셋
                                continue;
                            }
                        }
                    }

                    // 다중 평면 제약 슬라이딩 (Oilver's method)
                    if (planeCount == 0)
                    {
                        // 첫 번째 평면: 일반 슬라이딩
                        plane0Normal = hitNormal;
                        planeCount = 1;
                        remainingMotion = leftoverMotion - hitNormal * Vector3.Dot(leftoverMotion, hitNormal);
                    }
                    else if (planeCount == 1)
                    {
                        // 두 번째 평면: 크리스 (두 평면의 교선) 방향으로 투영
                        // 같은 방향 법선이면 단일 평면 슬라이딩 유지
                        if (Vector3.Dot(hitNormal, plane0Normal) > 0.99f)
                        {
                            remainingMotion = leftoverMotion - hitNormal * Vector3.Dot(leftoverMotion, hitNormal);
                        }
                        else
                        {
                            plane1Normal = hitNormal;
                            planeCount = 2;
                            Vector3 crease = Vector3.Cross(plane0Normal, plane1Normal);
                            float creaseSqr = crease.sqrMagnitude;
                            if (creaseSqr > 0.0001f)
                            {
                                crease = crease / Mathf.Sqrt(creaseSqr);
                                float projected = Vector3.Dot(leftoverMotion, crease);
                                remainingMotion = crease * projected;
                            }
                            else
                            {
                                remainingMotion = Vector3.zero;
                            }
                        }
                    }
                    else
                    {
                        // 3개 이상 평면 제약 — 더 이상 이동 불가
                        remainingMotion = Vector3.zero;
                    }

                    // 슬라이딩 결과 모션이 충돌 평면 안쪽을 향하면 제거 (jitter 방지)
                    // 부동소수점 오차나 법선 미세 변동으로 슬라이딩 후에도
                    // 벽 안쪽으로 향하는 성분이 남을 수 있다.
                    if (remainingMotion.sqrMagnitude > 0.0001f)
                    {
                        // 현재 충돌한 평면 체크
                        if (Vector3.Dot(remainingMotion, hitNormal) < 0f)
                        {
                            remainingMotion -= hitNormal * Vector3.Dot(remainingMotion, hitNormal);
                        }
                        // 이전 평면도 체크 (2평면 crease 투영 결과가 이전 평면을 침범할 수 있음)
                        if (planeCount >= 2 && Vector3.Dot(remainingMotion, plane0Normal) < 0f)
                        {
                            remainingMotion -= plane0Normal * Vector3.Dot(remainingMotion, plane0Normal);
                        }
                    }
                }
                else
                {
                    // Step 성공 시 남은 모션은 소진됨
                    break;
                }
            }

            // 5.5. 이전 프레임 접촉 법선 보존
            // 현재 프레임에서 벽 충돌이 감지되면 갱신.
            // 현재 프레임에서 벽 충돌이 없으면:
            //   - 이전 법선으로 사전 제거했기 때문에 충돌이 안 된 것일 수 있고,
            //   - 실제로 벽이 없어서(벽 경계를 넘음) 충돌이 안 된 것일 수도 있다.
            //   - 이를 구분하기 위해 벽 법선 방향으로 짧은 probe sweep을 수행한다.
            //   - 벽이 실제로 존재하면 법선을 유지하고, 없으면 클리어한다.
            if (curContactCount > 0)
            {
                // 현재 프레임에서 새 벽 충돌 감지 → 갱신
                _prevContactCount = curContactCount;
                _prevContactNormal0 = curContactNormal0;
                _prevContactNormal1 = curContactNormal1;
            }
            else if (_prevContactCount > 0)
            {
                // 현재 프레임에서 벽 충돌 없음 — 벽이 실제로 아직 있는지 probe
                float dotOriginal = Vector3.Dot(motion, _prevContactNormal0);
                if (dotOriginal >= -0.001f)
                {
                    // 원래 motion이 벽을 향하지 않음 → 유저가 방향을 바꿈 → 리셋
                    _prevContactCount = 0;
                    _prevContactNormal0 = Vector3.zero;
                    _prevContactNormal1 = Vector3.zero;
                }
                else
                {
                    // 원래 motion이 벽을 향했지만 사전 제거로 충돌 안 됨
                    // → 벽 법선 반대 방향(벽을 향해)으로 짧은 probe sweep
                    Vector3 probeDir = -_prevContactNormal0;
                    float probeDist = skinWidth * 3f; // skinWidth의 3배 거리로 probe
                    bool wallStillExists = mgr.World3D.SweepCapsule(
                        ToSys(position),
                        orientation,
                        sweepRadius,
                        halfLength,
                        ToSys(probeDir),
                        probeDist,
                        out SweepHit _,
                        excludeHandles);

                    if (!wallStillExists)
                    {
                        // 벽이 없음 — 벽 경계를 넘었음 → 리셋
                        _prevContactCount = 0;
                        _prevContactNormal0 = Vector3.zero;
                        _prevContactNormal1 = Vector3.zero;
                    }
                    // else: 벽이 여전히 존재 → 법선 유지
                }
            }

            // 6. Transform 갱신 (center 오프셋 제거)
            // position = transform.TransformPoint(center) = transform.position + rotation * scaledCenter
            // → transform.position = position - rotation * scaledCenter
            {
                var rot = transform.rotation;
                Vector3 scaledCenter = new Vector3(center.x * s.x, center.y * s.y, center.z * s.z);
                transform.position = position - (rot * scaledCenter);
            }

            // 7. Static body pose 갱신
            if (_kinematicHandle.HasValue)
            {
                mgr.World3D.SetStaticPose(
                    _kinematicHandle.Value,
                    ToSys(transform.TransformPoint(center)),
                    ToSysQ(transform.rotation));
            }

            // 8. Velocity 계산
            Vector3 finalPosition = transform.TransformPoint(center);
            if (Time.deltaTime > 0f)
                velocity = (finalPosition - startPosition) / Time.deltaTime;
            else
                velocity = Vector3.zero;

            return collisionFlags;
        }

        /// <summary>중력 자동 적용 이동. speed는 XZ 평면 속도.</summary>
        public bool SimpleMove(Vector3 speed)
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return isGrounded;

            // XZ 이동: speed * deltaTime
            Vector3 xzMotion = new Vector3(speed.x * dt, 0f, speed.z * dt);

            // 중력 누적
            _simpleMoveVerticalSpeed += Physics.gravity.y * dt;

            // 바닥에 있으면 수직 속도를 약간의 음수로 리셋 (바닥 접착)
            if (isGrounded)
                _simpleMoveVerticalSpeed = -0.5f;

            // 최종 motion 조합
            Vector3 motion = new Vector3(xzMotion.x, _simpleMoveVerticalSpeed * dt, xzMotion.z);

            Move(motion);

            return isGrounded;
        }

        // ── 같은 GO의 static handle 수집 ──

        private StaticHandle[] CollectSameGameObjectStaticHandles()
        {
            var handles = new List<StaticHandle>();
            if (_kinematicHandle.HasValue)
                handles.Add(_kinematicHandle.Value);

            foreach (var comp in gameObject.GetComponents<Collider>())
            {
                if (comp == this) continue;
                if (comp._staticHandle.HasValue && !handles.Contains(comp._staticHandle.Value))
                    handles.Add(comp._staticHandle.Value);
            }
            return handles.ToArray();
        }

        // ── Overlap Recovery (겹침 보정) ──

        private const int MAX_OVERLAP_RECOVERY_ITERATIONS = 4;
        private const float DEPENETRATION_SPEED = 1.0f;

        /// <summary>겹침이 있으면 밀어내기 벡터를 적용하여 depenetration합니다.</summary>
        private void PerformOverlapRecovery(IronRose.Engine.PhysicsManager mgr)
        {
            if (!_kinematicHandle.HasValue) return;

            var s = transform.lossyScale;
            float radiusScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
            float scaleY = Mathf.Abs(s.y);
            float scaledRadius = radius * radiusScale;
            // sweep과 동일한 radius 사용 (skinWidth 제거) — full radius 사용 시 바닥 떨림 발생
            float overlapRadius = Mathf.Max(0.01f, scaledRadius - skinWidth);
            float halfLength = Mathf.Max(0.01f, height * scaleY - 2f * scaledRadius) / 2f;
            SysQuaternion orientation = ToSysQ(transform.rotation);

            Span<CollidableReference> results = stackalloc CollidableReference[8];

            for (int iter = 0; iter < MAX_OVERLAP_RECOVERY_ITERATIONS; iter++)
            {
                Vector3 capsuleCenter = transform.TransformPoint(center);

                int count = mgr.World3D.OverlapCapsule(
                    ToSys(capsuleCenter),
                    orientation,
                    overlapRadius,
                    halfLength,
                    results,
                    _kinematicHandle);

                if (count == 0) break;

                // 겹침 방향을 정밀하게 구하기 어려우므로 위로 밀어내기
                Vector3 totalPush = Vector3.up * (skinWidth * DEPENETRATION_SPEED);

                transform.position += totalPush;

                if (_kinematicHandle.HasValue)
                {
                    mgr.World3D.SetStaticPose(
                        _kinematicHandle.Value,
                        ToSys(transform.TransformPoint(center)),
                        orientation);
                }
            }
        }

        // ── Step 처리 (계단 오르기) ──

        /// <summary>계단 오르기를 시도합니다. 성공 시 position을 갱신하고 true를 반환합니다.</summary>
        private bool TryStepUp(
            IronRose.Engine.PhysicsManager mgr,
            ref Vector3 position,
            SysQuaternion orientation,
            float sweepRadius,
            float halfLength,
            Vector3 forwardDir,
            float forwardDist,
            float scaleY)
        {
            float scaledStepOffset = stepOffset * scaleY;
            if (scaledStepOffset < 0.001f || forwardDist < 0.001f)
                return false;

            // 1. 위로 stepOffset만큼 sweep (천장 확인)
            bool hitUp = mgr.World3D.SweepCapsule(
                ToSys(position), orientation,
                sweepRadius, halfLength,
                new SysVector3(0, 1, 0), scaledStepOffset,
                out SweepHit upHit,
                _kinematicHandle);

            float actualStepUp = hitUp
                ? Mathf.Max(0f, upHit.T * scaledStepOffset - skinWidth)
                : scaledStepOffset;

            if (actualStepUp < 0.01f)
                return false;

            Vector3 upPosition = position + Vector3.up * actualStepUp;

            // 2. 올라간 위치에서 전방으로 sweep
            bool hitForward = mgr.World3D.SweepCapsule(
                ToSys(upPosition), orientation,
                sweepRadius, halfLength,
                ToSys(forwardDir), forwardDist,
                out SweepHit fwdHit,
                _kinematicHandle);

            float actualForward = hitForward
                ? Mathf.Max(0f, fwdHit.T * forwardDist - skinWidth)
                : forwardDist;

            if (actualForward < 0.001f)
                return false;

            Vector3 fwdPosition = upPosition + forwardDir * actualForward;

            // 3. 전방 이동 후 아래로 sweep (바닥 찾기)
            float downDist = actualStepUp + skinWidth * 2f;
            bool hitDown = mgr.World3D.SweepCapsule(
                ToSys(fwdPosition), orientation,
                sweepRadius, halfLength,
                new SysVector3(0, -1, 0), downDist,
                out SweepHit downHit,
                _kinematicHandle);

            if (!hitDown)
                return false;

            float actualDown = Mathf.Max(0f, downHit.T * downDist - skinWidth);
            Vector3 finalPosition = fwdPosition + Vector3.down * actualDown;

            // 바닥 법선 확인 — 계단 위가 경사면이 아닌지 체크
            Vector3 downNormal = FromSys(downHit.Normal);
            float downDotUp = Vector3.Dot(downNormal, Vector3.up);
            if (downDotUp < GROUND_NORMAL_THRESHOLD)
                return false;

            // 최종 높이가 원래보다 높아야 계단 오르기로 인정
            if (finalPosition.y <= position.y + 0.001f)
                return false;

            position = finalPosition;
            return true;
        }

        // ── OnControllerColliderHit 콜백 ──

        private void FireControllerColliderHit(
            Vector3 point, Vector3 normal, Vector3 moveDir, float moveLen,
            object? hitUserData)
        {
            var hitInfo = new ControllerColliderHit();
            hitInfo.controller = this;
            hitInfo.point = point;
            hitInfo.normal = normal;
            hitInfo.moveDirection = moveDir;
            hitInfo.moveLength = moveLen;

            if (hitUserData is Collider hitCol)
            {
                hitInfo.collider = hitCol;
                hitInfo.gameObject = hitCol.gameObject;
                hitInfo.transform = hitCol.transform;
                hitInfo.rigidbody = hitCol.gameObject.GetComponent<Rigidbody>();
            }
            else if (hitUserData is Rigidbody hitRb)
            {
                hitInfo.gameObject = hitRb.gameObject;
                hitInfo.transform = hitRb.transform;
                hitInfo.rigidbody = hitRb;
                hitInfo.collider = hitRb.gameObject.GetComponent<Collider>()!;
            }
            else
            {
                // UserData가 없거나 알 수 없는 타입이면 콜백 생략
                return;
            }

            foreach (var mb in gameObject.GetComponents<MonoBehaviour>())
            {
                if (mb.enabled && mb._hasAwoken)
                {
                    try
                    {
                        mb.OnControllerColliderHit(hitInfo);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[CharacterController] Exception in OnControllerColliderHit: {ex.Message}");
                    }
                }
            }
        }

        // ── Collider 오버라이드 ──

        internal override void RegisterAsStatic(IronRose.Engine.PhysicsManager mgr)
        {
            if (_staticRegistered) return;
            var s = transform.lossyScale;
            float radiusScale = Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
            float scaledRadius = radius * radiusScale;
            float scaledHeight = height * Mathf.Abs(s.y);
            float halfLength = Mathf.Max(0.01f, scaledHeight - 2f * scaledRadius) / 2f;
            _staticHandle = mgr.World3D.AddStaticCapsule(
                GetWorldPosition(), GetWorldRotation(),
                scaledRadius, halfLength);
            _kinematicHandle = _staticHandle;
            mgr.World3D.SetStaticUserData(_staticHandle.Value, this);
            _staticRegistered = true;
        }

        /// <summary>PhysicsManager에서 호출. static body pose를 현재 Transform에 맞게 동기화합니다.</summary>
        internal void SyncStaticPose(IronRose.Engine.PhysicsManager mgr)
        {
            if (!_kinematicHandle.HasValue) return;
            mgr.World3D.SetStaticPose(
                _kinematicHandle.Value,
                GetWorldPosition(),
                GetWorldRotation());
        }

        internal override void OnAddedToGameObject()
        {
            base.OnAddedToGameObject();
        }

        internal override void OnComponentDestroy()
        {
            _kinematicHandle = null;
            base.OnComponentDestroy();
        }

        // ── Gizmo ──

        public override void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.5f, 1f, 0.5f, 1f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            // skinWidth を含む外枠
            Gizmos.DrawWireCapsule(center, radius + skinWidth, height);
            // 実際の衝突カプセル
            Gizmos.color = new Color(0.5f, 1f, 0.5f, 0.5f);
            Gizmos.DrawWireCapsule(center, radius, height);
        }
    }
}
