#version 450

layout(location = 0) in vec3 Position;
layout(location = 1) in vec3 Normal;   // unused, must match vertex layout
layout(location = 2) in vec2 UV;       // unused, must match vertex layout

layout(set = 0, binding = 0) uniform Transforms
{
    mat4 World;
    mat4 ViewProjection;
    mat4 ViewMatrix;
};

void main()
{
    vec4 worldPos = World * vec4(Position, 1.0);
    gl_Position = ViewProjection * worldPos;
}
