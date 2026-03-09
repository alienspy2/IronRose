#version 450

layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;
layout(location = 2) in vec2 UV;

layout(set = 0, binding = 0) uniform OutlineData
{
    mat4 World;
    mat4 ViewProjection;
    vec4 OutlineColor;
    vec4 CameraPos;
    float OutlineWidth;
    float _pad1;
    float _pad2;
    float _pad3;
};

void main()
{
    vec4 worldPos = World * vec4(Position, 1.0);
    vec4 clipPos = ViewProjection * worldPos;

    if (OutlineWidth > 0.0)
    {
        // Expand outward from object center in clip space
        // Works for all geometry including flat planes
        vec4 centerWorld = World * vec4(0.0, 0.0, 0.0, 1.0);
        vec4 centerClip = ViewProjection * centerWorld;

        vec2 ndcVertex = clipPos.xy / clipPos.w;
        vec2 ndcCenter = centerClip.xy / centerClip.w;

        vec2 dir = ndcVertex - ndcCenter;
        float len = length(dir);
        if (len > 0.0001)
        {
            dir /= len;
            // OutlineWidth is in NDC units, scaled by clipPos.w for perspective correctness
            clipPos.xy += dir * OutlineWidth * clipPos.w;
        }
    }

    gl_Position = clipPos;
}
