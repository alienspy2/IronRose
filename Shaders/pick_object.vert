#version 450

layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;
layout(location = 2) in vec2 UV;

layout(set = 0, binding = 0) uniform PickData
{
    mat4 World;
    mat4 ViewProjection;
    uint ObjectId;
    uint _pad1;
    uint _pad2;
    uint _pad3;
};

void main()
{
    vec4 worldPos = World * vec4(Position, 1.0);
    gl_Position = ViewProjection * worldPos;
}
