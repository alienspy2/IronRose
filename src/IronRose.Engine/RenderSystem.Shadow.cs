using System;
using System.Collections.Generic;
using System.Numerics;
using Veldrid;
using RoseEngine;
using Vector4 = System.Numerics.Vector4;

namespace IronRose.Rendering
{
    // Shadow atlas rendering: generation, atlas packing, and VSM blur.
    // Extracted from RenderSystem (Phase 15 — H-1).
    public partial class RenderSystem
    {
        private static readonly System.Numerics.Vector3[] _cubeFaceTargets =
        {
            System.Numerics.Vector3.UnitX,
            -System.Numerics.Vector3.UnitX,
            System.Numerics.Vector3.UnitY,
            -System.Numerics.Vector3.UnitY,
            System.Numerics.Vector3.UnitZ,
            -System.Numerics.Vector3.UnitZ,
        };
        private static readonly System.Numerics.Vector3[] _cubeFaceUps =
        {
            -System.Numerics.Vector3.UnitY,
            -System.Numerics.Vector3.UnitY,
            System.Numerics.Vector3.UnitZ,
            -System.Numerics.Vector3.UnitZ,
            -System.Numerics.Vector3.UnitY,
            -System.Numerics.Vector3.UnitY,
        };

        private void ComputeShadowVP(Light light, Camera camera, out System.Numerics.Matrix4x4 lightVP)
        {
            if (light.type == LightType.Directional)
            {
                var camPos = camera.transform.position;
                var lightDir = light.transform.forward;
                float shadowRange = 20f;
                var eye = new System.Numerics.Vector3(
                    camPos.x - lightDir.x * shadowRange,
                    camPos.y - lightDir.y * shadowRange,
                    camPos.z - lightDir.z * shadowRange);
                var target = new System.Numerics.Vector3(camPos.x, camPos.y, camPos.z);
                var lightView = System.Numerics.Matrix4x4.CreateLookAt(eye, target, System.Numerics.Vector3.UnitY);
                var lightProj = System.Numerics.Matrix4x4.CreateOrthographic(
                    shadowRange * 2, shadowRange * 2, 0.1f, shadowRange * 2);
                lightVP = lightView * lightProj;
            }
            else if (light.type == LightType.Spot)
            {
                var pos = light.transform.position;
                var fwd = light.transform.forward;
                var eye = new System.Numerics.Vector3(pos.x, pos.y, pos.z);
                var target = new System.Numerics.Vector3(pos.x + fwd.x, pos.y + fwd.y, pos.z + fwd.z);
                var lightView = System.Numerics.Matrix4x4.CreateLookAt(eye, target, System.Numerics.Vector3.UnitY);
                var lightProj = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(
                    light.spotOuterAngle * MathF.PI / 180f, 1f, light.shadowNearPlane, light.range);
                lightVP = lightView * lightProj;
            }
            else
            {
                lightVP = System.Numerics.Matrix4x4.Identity;
            }
        }

        private void RenderShadowPass(CommandList cl, Camera camera)
        {
            if (_shadowAtlasBackCullPipeline == null || _shadowLayout == null) return;

            _frameShadows.Clear();
            _atlasPackX = 0;
            _atlasPackY = 0;
            _atlasRowHeight = 0;

            bool hasShadowLights = false;
            var checkLightSnap = Light._allLights.Snapshot();
            foreach (var light in checkLightSnap)
            {
                if (light.enabled && light.shadows)
                { hasShadowLights = true; break; }
            }
            if (!hasShadowLights) return;

            cl.SetFramebuffer(_atlasFramebuffer);
            cl.ClearColorTarget(0, new RgbaFloat(1f, 1f, 1f, 1f));
            cl.ClearDepthStencil(1f);
            var shadowResourceSet = _device!.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                _shadowLayout, _shadowTransformBuffer));

            var shadowLightSnap = Light._allLights.Snapshot();
            foreach (var light in shadowLightSnap)
            {
                if (!light.enabled || !light.shadows) continue;

                cl.SetPipeline(light.shadowCullMode switch
                {
                    ShadowCullMode.Front   => _shadowAtlasFrontCullPipeline!,
                    ShadowCullMode.Back    => _shadowAtlasBackCullPipeline!,
                    ShadowCullMode.TwoFace => _shadowAtlasNoCullPipeline!,
                    _ => _shadowAtlasNoCullPipeline!,
                });

                if (light.type == LightType.Point)
                    RenderPointShadowToAtlas(cl, light, shadowResourceSet);
                else
                    RenderDirSpotShadowToAtlas(cl, light, camera, shadowResourceSet);
            }

            shadowResourceSet.Dispose();
        }

        private bool AllocateAtlasTile(int size, out int tileX, out int tileY)
        {
            if (_atlasPackX + size > AtlasSize)
            {
                _atlasPackX = 0;
                _atlasPackY += _atlasRowHeight;
                _atlasRowHeight = 0;
            }
            if (_atlasPackY + size > AtlasSize)
            {
                tileX = 0;
                tileY = 0;
                return false;
            }
            tileX = _atlasPackX;
            tileY = _atlasPackY;
            _atlasPackX += size;
            _atlasRowHeight = Math.Max(_atlasRowHeight, size);
            return true;
        }

        private static Vector4 ComputeAtlasParams(int tileX, int tileY, int tileSize)
        {
            return new Vector4(
                (float)tileX / AtlasSize, (float)tileY / AtlasSize,
                (float)tileSize / AtlasSize, (float)tileSize / AtlasSize);
        }

        private void RenderDirSpotShadowToAtlas(CommandList cl, Light light, Camera camera, ResourceSet shadowResourceSet)
        {
            int res = light.shadowResolution;
            if (!AllocateAtlasTile(res, out int tileX, out int tileY))
                return;

            ComputeShadowVP(light, camera, out var lightVP);
            var atlasParams = ComputeAtlasParams(tileX, tileY, res);

            var depthParams = (light.type == LightType.Spot)
                ? new Vector4(1, light.shadowNearPlane, light.range, 0)
                : Vector4.Zero;

            cl.SetViewport(0, new Viewport((uint)tileX, (uint)tileY, (uint)res, (uint)res, 0, 1));
            cl.SetScissorRect(0, (uint)tileX, (uint)tileY, (uint)res, (uint)res);

            var shadowMeshSnap = MeshRenderer._allRenderers.Snapshot();
            foreach (var renderer in shadowMeshSnap)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer.gameObject._isEditorInternal) continue;
                if ((renderer.material?.blendMode ?? BlendMode.Opaque) != BlendMode.Opaque) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter?.mesh == null) continue;
                var mesh = filter.mesh;
                if (mesh.VertexBuffer == null || mesh.IndexBuffer == null) continue;

                var t = renderer.transform;
                var world = RoseEngine.Matrix4x4.TRS(t.position, t.rotation, t.lossyScale).ToNumerics();
                var mvp = world * lightVP;

                cl.UpdateBuffer(_shadowTransformBuffer, 0, new ShadowTransformUniforms { LightMVP = mvp, DepthParams = depthParams });
                cl.SetGraphicsResourceSet(0, shadowResourceSet);
                cl.SetVertexBuffer(0, mesh.VertexBuffer);
                cl.SetIndexBuffer(mesh.IndexBuffer, IndexFormat.UInt32);
                cl.DrawIndexed((uint)mesh.indices.Length);
            }

            _frameShadows[light] = new FrameShadowTile
            {
                LightVP = lightVP,
                AtlasParams = atlasParams,
            };
        }

        private void RenderPointShadowToAtlas(CommandList cl, Light light, ResourceSet shadowResourceSet)
        {
            int res = light.shadowResolution;
            var faceVPs = new System.Numerics.Matrix4x4[6];
            var faceAtlasParams = new Vector4[6];

            for (int face = 0; face < 6; face++)
            {
                if (!AllocateAtlasTile(res, out int tileX, out int tileY))
                    return;
                faceAtlasParams[face] = ComputeAtlasParams(tileX, tileY, res);
            }

            var pos = light.transform.position;
            var eye = new System.Numerics.Vector3(pos.x, pos.y, pos.z);
            var faceProj = System.Numerics.Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 2f, 1f, light.shadowNearPlane, light.range);

            for (int face = 0; face < 6; face++)
            {
                var faceView = System.Numerics.Matrix4x4.CreateLookAt(
                    eye, eye + _cubeFaceTargets[face], _cubeFaceUps[face]);
                faceVPs[face] = faceView * faceProj;
            }

            var depthParams = new Vector4(1, light.shadowNearPlane, light.range, 0);

            var pointShadowMeshSnap = MeshRenderer._allRenderers.Snapshot();
            for (int face = 0; face < 6; face++)
            {
                var ap = faceAtlasParams[face];
                int tileX = (int)(ap.X * AtlasSize);
                int tileY = (int)(ap.Y * AtlasSize);

                cl.SetViewport(0, new Viewport((uint)tileX, (uint)tileY, (uint)res, (uint)res, 0, 1));
                cl.SetScissorRect(0, (uint)tileX, (uint)tileY, (uint)res, (uint)res);

                foreach (var renderer in pointShadowMeshSnap)
                {
                    if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                    if (renderer.gameObject._isEditorInternal) continue;
                    if ((renderer.material?.blendMode ?? BlendMode.Opaque) != BlendMode.Opaque) continue;
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (filter?.mesh == null) continue;
                    var mesh = filter.mesh;
                    if (mesh.VertexBuffer == null || mesh.IndexBuffer == null) continue;

                    var t = renderer.transform;
                    var world = RoseEngine.Matrix4x4.TRS(t.position, t.rotation, t.lossyScale).ToNumerics();
                    var mvp = world * faceVPs[face];

                    cl.UpdateBuffer(_shadowTransformBuffer, 0, new ShadowTransformUniforms { LightMVP = mvp, DepthParams = depthParams });
                    cl.SetGraphicsResourceSet(0, shadowResourceSet);
                    cl.SetVertexBuffer(0, mesh.VertexBuffer);
                    cl.SetIndexBuffer(mesh.IndexBuffer, IndexFormat.UInt32);
                    cl.DrawIndexed((uint)mesh.indices.Length);
                }
            }

            _frameShadows[light] = new FrameShadowTile
            {
                FaceVPs = faceVPs,
                FaceAtlasParams = faceAtlasParams,
            };
        }

        private void BlurShadowAtlas(CommandList cl)
        {
            if (_shadowBlurPipeline == null || _shadowBlurSetH == null || _shadowBlurSetV == null)
                return;

            foreach (var (light, tile) in _frameShadows)
            {
                float softness = light.shadowSoftness;
                if (softness <= 0f) continue;

                if (light.type == LightType.Point && tile.FaceAtlasParams != null)
                {
                    for (int face = 0; face < 6; face++)
                        BlurTile(cl, tile.FaceAtlasParams[face], softness);
                }
                else
                {
                    BlurTile(cl, tile.AtlasParams, softness);
                }
            }
        }

        private void BlurTile(CommandList cl, Vector4 tileParams, float softness)
        {
            int tileX = (int)(tileParams.X * AtlasSize);
            int tileY = (int)(tileParams.Y * AtlasSize);
            int tileW = (int)(tileParams.Z * AtlasSize);
            int tileH = (int)(tileParams.W * AtlasSize);
            if (tileW <= 0 || tileH <= 0) return;

            cl.SetPipeline(_shadowBlurPipeline);

            // H pass: atlas → temp
            cl.SetFramebuffer(_atlasTempFramebuffer);
            cl.SetViewport(0, new Viewport((uint)tileX, (uint)tileY, (uint)tileW, (uint)tileH, 0, 1));
            cl.UpdateBuffer(_shadowBlurBuffer, 0, new ShadowBlurParams
            {
                Direction = new Vector4(softness / AtlasSize, 0, 0, 0),
                TileParams = tileParams,
            });
            cl.SetGraphicsResourceSet(0, _shadowBlurSetH);
            cl.Draw(3, 1, 0, 0);

            // V pass: temp → atlas
            cl.SetFramebuffer(_atlasBlurFramebuffer);
            cl.SetViewport(0, new Viewport((uint)tileX, (uint)tileY, (uint)tileW, (uint)tileH, 0, 1));
            cl.UpdateBuffer(_shadowBlurBuffer, 0, new ShadowBlurParams
            {
                Direction = new Vector4(0, softness / AtlasSize, 0, 0),
                TileParams = tileParams,
            });
            cl.SetGraphicsResourceSet(0, _shadowBlurSetV);
            cl.Draw(3, 1, 0, 0);
        }
    }
}
