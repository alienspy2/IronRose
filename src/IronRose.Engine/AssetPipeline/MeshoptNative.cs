using System;
using System.Runtime.InteropServices;

namespace IronRose.AssetPipeline
{
    /// <summary>
    /// Meshoptimizer.NET 패키지가 노출하지 않는 네이티브 함수에 대한 추가 P/Invoke 바인딩.
    /// 네이티브 라이브러리("meshoptimizer")는 NuGet 패키지가 이미 제공한다.
    /// 번들 버전: v0.21+ (meshopt_simplifyWithAttributes에 vertex_lock 파라미터 포함).
    /// </summary>
    internal static partial class MeshoptNative
    {
        private const string LibName = "meshoptimizer";

        /// <summary>
        /// 메시 단순화 (attribute 고려, v0.21+ — vertex_lock 포함).
        /// vertex_lock은 IntPtr.Zero로 전달하면 잠금 없음.
        /// 항상 원본 메시로부터 호출해야 한다 (체이닝 금지).
        /// </summary>
        [DllImport(LibName, EntryPoint = "meshopt_simplifyWithAttributes", CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint SimplifyWithAttributes(
            uint[] destination,
            uint[] indices,
            nuint indexCount,
            float[] vertexPositions,
            nuint vertexCount,
            nuint vertexPositionsStride,
            float[] vertexAttributes,
            nuint vertexAttributesStride,
            float[] attributeWeights,
            nuint attributeCount,
            IntPtr vertexLock,
            nuint targetIndexCount,
            float targetError,
            uint options,
            out float resultError);

        /// <summary>메시 단순화 (기본 — position만 고려, vertex_lock 없음).</summary>
        [DllImport(LibName, EntryPoint = "meshopt_simplify", CallingConvention = CallingConvention.Cdecl)]
        public static extern nuint Simplify(
            uint[] destination,
            uint[] indices,
            nuint indexCount,
            float[] vertexPositions,
            nuint vertexCount,
            nuint vertexPositionsStride,
            nuint targetIndexCount,
            float targetError,
            uint options,
            out float resultError);

        /// <summary>
        /// 정점 중복 제거를 위한 리맵 테이블 생성.
        /// 반환값 = 유니크 정점 수.
        /// </summary>
        [DllImport(LibName, EntryPoint = "meshopt_generateVertexRemap", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe nuint GenerateVertexRemap(
            uint[] destination,
            uint[]? indices,
            nuint indexCount,
            void* vertices,
            nuint vertexCount,
            nuint vertexSize);

        /// <summary>리맵 테이블을 사용하여 인덱스 버퍼 재매핑.</summary>
        [DllImport(LibName, EntryPoint = "meshopt_remapIndexBuffer", CallingConvention = CallingConvention.Cdecl)]
        public static extern void RemapIndexBuffer(
            uint[] destination,
            uint[]? indices,
            nuint indexCount,
            uint[] remap);

        /// <summary>리맵 테이블을 사용하여 정점 버퍼 재매핑. 반환값 = 유니크 정점 수.</summary>
        [DllImport(LibName, EntryPoint = "meshopt_remapVertexBuffer", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe void RemapVertexBuffer(
            void* destination,
            void* vertices,
            nuint vertexCount,
            nuint vertexSize,
            uint[] remap);

        /// <summary>오버드로우 최적화 (삼각형 순서 재배치).</summary>
        [DllImport(LibName, EntryPoint = "meshopt_optimizeOverdraw", CallingConvention = CallingConvention.Cdecl)]
        public static extern void OptimizeOverdraw(
            uint[] destination,
            uint[] indices,
            nuint indexCount,
            float[] vertexPositions,
            nuint vertexCount,
            nuint vertexPositionsStride,
            float threshold);

        /// <summary>정점 페치 최적화 (메모리 접근 지역성 개선). 반환값 = 유니크 정점 수.</summary>
        [DllImport(LibName, EntryPoint = "meshopt_optimizeVertexFetch", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe nuint OptimizeVertexFetch(
            void* destination,
            uint[] indices,
            nuint indexCount,
            void* vertices,
            nuint vertexCount,
            nuint vertexSize);

        /// <summary>단순화 오류 스케일 계산.</summary>
        [DllImport(LibName, EntryPoint = "meshopt_simplifyScale", CallingConvention = CallingConvention.Cdecl)]
        public static extern float SimplifyScale(
            float[] vertexPositions,
            nuint vertexCount,
            nuint vertexPositionsStride);
    }
}
