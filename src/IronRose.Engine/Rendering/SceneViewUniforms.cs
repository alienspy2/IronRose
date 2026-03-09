using System.Numerics;
using System.Runtime.InteropServices;

namespace IronRose.Rendering
{
    public enum SceneViewRenderMode
    {
        Wireframe,
        MatCap,
        DiffuseOnly,
        Rendered,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SceneViewTransformUniforms
    {
        public Matrix4x4 World;
        public Matrix4x4 ViewProjection;
        public Matrix4x4 ViewMatrix; // MatCap용 뷰 공간 법선 변환
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SceneViewMaterialUniforms
    {
        public Vector4 Color;
        public float HasTexture;
        public float Metallic;
        public float Roughness;
        private float _pad3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SceneViewLightUniforms
    {
        public Vector4 CameraPos;
        public Vector4 LightDir;    // DiffuseOnly용 단일 디렉셔널 라이트
        public Vector4 LightColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PickIdUniforms
    {
        public Matrix4x4 World;
        public Matrix4x4 ViewProjection;
        public uint ObjectId;
        private uint _pad1, _pad2, _pad3;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct OutlineUniforms
    {
        public Matrix4x4 World;
        public Matrix4x4 ViewProjection;
        public Vector4 OutlineColor;
        public Vector4 CameraPos;
        public float OutlineWidth;
        private float _pad1, _pad2, _pad3;
    }
}
