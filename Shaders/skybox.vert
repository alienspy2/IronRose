#version 450

// Fullscreen triangle (no vertex buffer needed)
vec2 positions[3] = vec2[](
    vec2(-1.0, -1.0),
    vec2( 3.0, -1.0),
    vec2(-1.0,  3.0)
);

layout(set = 0, binding = 0) uniform SkyboxUniforms
{
    mat4 InverseViewProjection;
    vec4 SunDirection;    // xyz = direction toward sun, w = unused
    vec4 SkyParams;       // x = zenithIntensity, y = horizonIntensity, z = sunAngularRadius, w = sunIntensity
    vec4 ZenithColor;     // rgb = zenith color
    vec4 HorizonColor;    // rgb = horizon color
    vec4 TextureParams;   // x = hasTexture, y = exposure, z = rotation (radians), w = unused
};

layout(location = 0) out vec3 fsin_RayDir;

void main()
{
    vec2 pos = positions[gl_VertexIndex];
    gl_Position = vec4(pos, 1.0, 1.0); // z=1.0 → max depth after perspective divide (1.0/1.0 = 1.0)

    // Camera basis vectors packed in mat4 columns by CPU:
    //   col0 = right * tanHalfFovX
    //   col1 = up    * tanHalfFovY
    //   col2 = forward
    vec3 rightScaled = InverseViewProjection[0].xyz;
    vec3 upScaled    = InverseViewProjection[1].xyz;
    vec3 forward     = InverseViewProjection[2].xyz;

    fsin_RayDir = forward + pos.x * rightScaled + pos.y * upScaled;
}
