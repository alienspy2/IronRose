// ------------------------------------------------------------
// @file    Ray.cs
// @brief   Unity API 호환 Ray struct. 원점(origin)과 방향(direction)으로 정의되는 반직선.
// @deps    Vector3
// @exports
//   struct Ray
//     origin: Vector3                          — 레이의 시작점
//     direction: Vector3                       — 레이의 방향 (정규화)
//     Ray(Vector3 origin, Vector3 direction)   — 생성자 (direction을 자동 정규화)
//     GetPoint(float distance): Vector3        — 레이 위의 특정 거리 지점 반환
//     ToString(): string                       — 디버그용 문자열 표현
// @note    Unity의 Ray와 동일한 인터페이스. 생성자에서 direction을 normalized로 저장한다.
// ------------------------------------------------------------
using System;

namespace RoseEngine
{
    public struct Ray
    {
        public Vector3 origin;
        public Vector3 direction;

        public Ray(Vector3 origin, Vector3 direction)
        {
            this.origin = origin;
            this.direction = direction.normalized;
        }

        public Vector3 GetPoint(float distance)
        {
            return origin + direction * distance;
        }

        public override string ToString()
        {
            return $"Origin: {origin}, Dir: {direction}";
        }
    }
}
