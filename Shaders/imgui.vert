#version 450

layout(location = 0) in vec2 Position;
layout(location = 1) in vec2 TexCoord;
layout(location = 2) in vec4 Color;

layout(location = 0) out vec2 fsin_TexCoord;
layout(location = 1) out vec4 fsin_Color;

layout(set = 0, binding = 0) uniform ProjectionBuffer
{
    mat4 Projection;
};

void main()
{
    fsin_TexCoord = TexCoord;
    fsin_Color = Color;
    gl_Position = Projection * vec4(Position, 0.0, 1.0);
}
