#version 450

layout(location = 0) in vec2 fsin_UV;
layout(location = 0) out vec4 fsout_Color;

layout(set = 0, binding = 0) uniform texture2D SourceTexture;
layout(set = 0, binding = 1) uniform sampler SourceSampler;

layout(set = 0, binding = 2) uniform BloomParams
{
    float Threshold;
    float SoftKnee;
    float _pad1;
    float _pad2;
};

void main()
{
    vec3 color = texture(sampler2D(SourceTexture, SourceSampler), fsin_UV).rgb;
    float brightness = dot(color, vec3(0.2126, 0.7152, 0.0722));

    float knee = Threshold * SoftKnee;
    float soft = brightness - Threshold + knee;
    soft = clamp(soft, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee + 0.00001);

    float contribution = max(soft, brightness - Threshold) / max(brightness, 0.00001);
    fsout_Color = vec4(color * max(contribution, 0.0), 1.0);
}
