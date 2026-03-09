#version 450

layout(location = 0) out uint out_ObjectId;

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
    out_ObjectId = ObjectId;
}
