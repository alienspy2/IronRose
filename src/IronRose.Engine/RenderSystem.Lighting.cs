using System;
using System.Numerics;
using Veldrid;
using RoseEngine;
using Vector4 = System.Numerics.Vector4;

namespace IronRose.Rendering
{
    // Deferred lighting, ambient/IBL, environment map, skybox, and forward light upload.
    // Extracted from RenderSystem (Phase 15 — H-1).
    public partial class RenderSystem
    {
        private static LightInfoGPU CollectLightInfo(Light light)
        {
            var info = new LightInfoGPU();
            if (light.type == LightType.Directional)
            {
                var forward = light.transform.forward;
                info.PositionOrDirection = new Vector4(forward.x, forward.y, forward.z, 0f);
            }
            else
            {
                var pos = light.transform.position;
                info.PositionOrDirection = new Vector4(pos.x, pos.y, pos.z, (float)light.type);
            }
            info.ColorIntensity = new Vector4(light.color.r, light.color.g, light.color.b, light.intensity);

            if (light.type == LightType.Spot)
            {
                float cosInner = MathF.Cos(light.spotAngle * 0.5f * MathF.PI / 180f);
                float cosOuter = MathF.Cos(light.spotOuterAngle * 0.5f * MathF.PI / 180f);
                info.Params = new Vector4(light.range, cosInner, cosOuter, light.rangeNear);
                var fwd = light.transform.forward;
                info.SpotDirection = new Vector4(fwd.x, fwd.y, fwd.z, light.shadowNearPlane);
            }
            else
            {
                info.Params = new Vector4(light.range, light.shadowNearPlane, 0, light.rangeNear);
            }
            return info;
        }

        private void UploadAmbientData(CommandList cl, Camera camera)
        {
            var camPos = camera.transform.position;
            var uniforms = new AmbientUniforms
            {
                CameraPos = new Vector4(camPos.x, camPos.y, camPos.z, 0),
                SkyAmbientColor = ComputeSkyAmbientColor(),
            };
            cl.UpdateBuffer(_ambientBuffer, 0, uniforms);
        }

        private void UploadSingleLightUniforms(CommandList cl, Light light, Camera camera,
            System.Numerics.Matrix4x4 viewProj)
        {
            var camPos = camera.transform.position;
            var lightInfo = CollectLightInfo(light);

            System.Numerics.Matrix4x4 mvp;
            if (light.type == LightType.Point)
            {
                var pos = light.transform.position;
                float scale = light.range * 2.0f * 1.05f;
                var world = RoseEngine.Matrix4x4.TRS(
                    new RoseEngine.Vector3(pos.x, pos.y, pos.z),
                    RoseEngine.Quaternion.identity,
                    new RoseEngine.Vector3(scale, scale, scale)).ToNumerics();
                mvp = world * viewProj;
            }
            else if (light.type == LightType.Spot)
            {
                var pos = light.transform.position;
                float height = light.range;
                float halfAngle = light.spotOuterAngle * 0.5f * MathF.PI / 180f;
                float baseRadius = height * MathF.Tan(halfAngle);
                var rotation = RoseEngine.Quaternion.FromToRotation(
                    RoseEngine.Vector3.forward, light.transform.forward);
                var world = RoseEngine.Matrix4x4.TRS(
                    new RoseEngine.Vector3(pos.x, pos.y, pos.z),
                    rotation,
                    new RoseEngine.Vector3(baseRadius, baseRadius, height)).ToNumerics();
                mvp = world * viewProj;
            }
            else
            {
                mvp = System.Numerics.Matrix4x4.Identity;
            }

            var lightVP = System.Numerics.Matrix4x4.Identity;
            var shadowParams = Vector4.Zero;
            var atlasParams = Vector4.Zero;

            var uniforms = new LightVolumeUniforms
            {
                WorldViewProjection = mvp,
                CameraPos = new Vector4(camPos.x, camPos.y, camPos.z, 0),
                ScreenParams = new Vector4(_activeCtx!.GBuffer!.Width, _activeCtx.GBuffer.Height, 0, 0),
                Light = lightInfo,
            };

            if (light.shadows && _frameShadows.TryGetValue(light, out var shadowTile))
            {
                shadowParams = new Vector4(1f, light.shadowBias, light.shadowNormalBias, light.shadowSoftness);

                if (light.type == LightType.Point && shadowTile.FaceVPs != null && shadowTile.FaceAtlasParams != null)
                {
                    uniforms.FaceVP0 = shadowTile.FaceVPs[0];
                    uniforms.FaceVP1 = shadowTile.FaceVPs[1];
                    uniforms.FaceVP2 = shadowTile.FaceVPs[2];
                    uniforms.FaceVP3 = shadowTile.FaceVPs[3];
                    uniforms.FaceVP4 = shadowTile.FaceVPs[4];
                    uniforms.FaceVP5 = shadowTile.FaceVPs[5];
                    uniforms.FaceAtlasParams0 = shadowTile.FaceAtlasParams[0];
                    uniforms.FaceAtlasParams1 = shadowTile.FaceAtlasParams[1];
                    uniforms.FaceAtlasParams2 = shadowTile.FaceAtlasParams[2];
                    uniforms.FaceAtlasParams3 = shadowTile.FaceAtlasParams[3];
                    uniforms.FaceAtlasParams4 = shadowTile.FaceAtlasParams[4];
                    uniforms.FaceAtlasParams5 = shadowTile.FaceAtlasParams[5];
                }
                else
                {
                    lightVP = shadowTile.LightVP;
                    atlasParams = shadowTile.AtlasParams;
                }
            }

            uniforms.LightViewProjection = lightVP;
            uniforms.ShadowParams = shadowParams;
            uniforms.ShadowAtlasParams = atlasParams;

            cl.UpdateBuffer(_lightVolumeBuffer, 0, uniforms);
        }

        private ResourceSet GetLightVolumeResourceSet(Light light)
        {
            return _atlasShadowSet!;
        }

        // ==============================
        // Environment map for deferred lighting
        // ==============================

        private void UpdateEnvMapForAmbient()
        {
            TextureView? envMapView = null;
            var skyboxMat = RenderSettings.skybox;
            if (skyboxMat?.shader?.name == "Skybox/Panoramic" && skyboxMat.mainTexture != null)
            {
                if (skyboxMat._cachedCubemap == null || skyboxMat._cachedCubemapSource != skyboxMat.mainTexture)
                {
                    try
                    {
                        skyboxMat._cachedCubemap?.Dispose();
                        int faceSize = Math.Clamp(skyboxMat.cubemapFaceSize, 128, 4096);
                        skyboxMat._cachedCubemap = Cubemap.CreateFromEquirectangular(skyboxMat.mainTexture, faceSize);
                        skyboxMat._cachedCubemap.UploadToGPU(_device!, generateMipmaps: true);
                        skyboxMat._cachedCubemapSource = skyboxMat.mainTexture;
                    }
                    catch (Exception ex)
                    {
                        EditorDebug.LogError($"[Skybox] Cubemap 생성 실패 — skybox를 제거합니다: {ex.Message}");
                        skyboxMat._cachedCubemap?.Dispose();
                        skyboxMat._cachedCubemap = null;
                        skyboxMat._cachedCubemapSource = null;
                        RenderSettings.skybox = null;
                        return;
                    }
                }
                envMapView = skyboxMat._cachedCubemap?.TextureView;
            }

            if (envMapView != _currentAmbientEnvMapView)
            {
                _ambientResourceSet?.Dispose();
                var texView = envMapView ?? _whiteCubemap!.TextureView!;
                _ambientResourceSet = _device!.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                    _ambientLayout!,
                    _ambientBuffer!,
                    texView,
                    _envMapBuffer!));
                _currentAmbientEnvMapView = envMapView;
            }
        }

        private void UploadEnvMapData(CommandList cl)
        {
            var skyboxMat = RenderSettings.skybox;
            bool usePanoramic = skyboxMat?.shader?.name == "Skybox/Panoramic" && skyboxMat.mainTexture != null;

            var sunDir = new Vector4(0.3f, 0.8f, 0.5f, 0f);
            foreach (var light in Light._allLights)
            {
                if (!light.enabled || !light.gameObject.activeInHierarchy) continue;
                if (light.type == LightType.Directional)
                {
                    var forward = light.transform.forward;
                    sunDir = new Vector4(-forward.x, -forward.y, -forward.z, 0f);
                    break;
                }
            }

            float exposure = skyboxMat?.exposure ?? 1.0f;
            float rotation = skyboxMat?.rotation ?? 0.0f;
            float rotationRad = rotation * (MathF.PI / 180f);

            var envUniforms = new EnvMapUniforms
            {
                TextureParams = new Vector4(usePanoramic ? 1f : 0f, exposure, rotationRad, 0f),
                SunDirection = sunDir,
                SkyParams = new Vector4(SkyZenithIntensity, SkyHorizonIntensity, DefaultSunAngularRadius, RenderSettings.sunIntensity),
                ZenithColor = SkyZenithColor,
                HorizonColor = SkyHorizonColor,
            };

            cl.UpdateBuffer(_envMapBuffer, 0, envUniforms);
        }

        // ==============================
        // Skybox rendering
        // ==============================

        private static Vector4 SkyZenithColor
        {
            get { var c = RenderSettings.skyZenithColor; return new Vector4(c.r, c.g, c.b, 1f); }
        }
        private static Vector4 SkyHorizonColor
        {
            get { var c = RenderSettings.skyHorizonColor; return new Vector4(c.r, c.g, c.b, 1f); }
        }
        private static float SkyZenithIntensity => RenderSettings.skyZenithIntensity;
        private static float SkyHorizonIntensity => RenderSettings.skyHorizonIntensity;
        private const float DefaultSunAngularRadius = 0.02f;

        private void RenderSkybox(CommandList cl, Camera camera, System.Numerics.Matrix4x4 viewProj)
        {
            if (_skyboxPipeline == null || _skyboxResourceSet == null) return;

            // Use camera basis vectors directly (no matrix inversion) for stable ray reconstruction.
            // Avoids numerical jitter from inverting the ViewProjection matrix.
            var camFwd = camera.transform.forward;
            var camRight = camera.transform.rotation * RoseEngine.Vector3.right;
            var camUp = camera.transform.rotation * RoseEngine.Vector3.up;
            float tanHalfFov = MathF.Tan(camera.fieldOfView * 0.5f * MathF.PI / 180f);
            float aspect = _activeCtx != null && _activeCtx.RenderHeight > 0
                ? (float)_activeCtx.RenderWidth / _activeCtx.RenderHeight
                : 16f / 9f;
            float tanX = tanHalfFov * aspect;
            float tanY = tanHalfFov;

            // Pack into mat4 layout — GLSL reads C# rows as columns:
            //   col0 = right * tanHalfFovX
            //   col1 = up    * tanHalfFovY
            //   col2 = forward
            var skyboxRayMatrix = new System.Numerics.Matrix4x4(
                camRight.x * tanX, camRight.y * tanX, camRight.z * tanX, 0,
                camUp.x * tanY,    camUp.y * tanY,    camUp.z * tanY,    0,
                camFwd.x,          camFwd.y,          camFwd.z,          0,
                0, 0, 0, 0);

            var sunDir = new Vector4(0.3f, 0.8f, 0.5f, 0f);
            foreach (var light in Light._allLights)
            {
                if (!light.enabled || !light.gameObject.activeInHierarchy) continue;
                if (light.type == LightType.Directional)
                {
                    var forward = light.transform.forward;
                    sunDir = new Vector4(-forward.x, -forward.y, -forward.z, 0f);
                    break;
                }
            }

            var skyboxMat = RenderSettings.skybox;
            bool usePanoramic = skyboxMat?.shader?.name == "Skybox/Panoramic" && skyboxMat.mainTexture != null;

            if (usePanoramic)
            {
                if (skyboxMat!._cachedCubemap == null || skyboxMat._cachedCubemapSource != skyboxMat.mainTexture)
                {
                    try
                    {
                        skyboxMat._cachedCubemap?.Dispose();
                        int faceSize = Math.Clamp(skyboxMat.cubemapFaceSize, 128, 4096);
                        skyboxMat._cachedCubemap = Cubemap.CreateFromEquirectangular(skyboxMat.mainTexture!, faceSize);
                        skyboxMat._cachedCubemap.UploadToGPU(_device!, generateMipmaps: true);
                        skyboxMat._cachedCubemapSource = skyboxMat.mainTexture;
                    }
                    catch (Exception ex)
                    {
                        EditorDebug.LogError($"[Skybox] Cubemap 생성 실패 — skybox를 제거합니다: {ex.Message}");
                        skyboxMat._cachedCubemap?.Dispose();
                        skyboxMat._cachedCubemap = null;
                        skyboxMat._cachedCubemapSource = null;
                        RenderSettings.skybox = null;
                        return;
                    }
                }
                var texView = skyboxMat._cachedCubemap?.TextureView;
                if (texView != null && texView != _currentSkyboxTextureView)
                {
                    _skyboxResourceSet?.Dispose();
                    _skyboxResourceSet = _device!.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                        _skyboxLayout!, _skyboxUniformBuffer!, texView, _defaultSampler!));
                    _currentSkyboxTextureView = texView;
                }
            }
            else if (_currentSkyboxTextureView != null)
            {
                _skyboxResourceSet?.Dispose();
                _skyboxResourceSet = _device!.ResourceFactory.CreateResourceSet(new ResourceSetDescription(
                    _skyboxLayout!, _skyboxUniformBuffer!, _whiteCubemap!.TextureView!, _defaultSampler!));
                _currentSkyboxTextureView = null;
            }

            float exposure = skyboxMat?.exposure ?? 1.0f;
            float rotation = skyboxMat?.rotation ?? 0.0f;
            float rotationRad = rotation * (MathF.PI / 180f);

            var uniforms = new SkyboxUniforms
            {
                InverseViewProjection = skyboxRayMatrix,
                SunDirection = sunDir,
                SkyParams = new Vector4(SkyZenithIntensity, SkyHorizonIntensity, DefaultSunAngularRadius, RenderSettings.sunIntensity),
                ZenithColor = SkyZenithColor,
                HorizonColor = SkyHorizonColor,
                TextureParams = new Vector4(usePanoramic ? 1f : 0f, exposure, rotationRad, 0f),
            };

            cl.UpdateBuffer(_skyboxUniformBuffer, 0, uniforms);

            cl.SetPipeline(_skyboxPipeline);
            cl.SetGraphicsResourceSet(0, _skyboxResourceSet);
            cl.Draw(3, 1, 0, 0);
        }

        private Vector4 ComputeSkyAmbientColor()
        {
            var skyboxMat = RenderSettings.skybox;
            float intensity = RenderSettings.ambientIntensity;

            if (skyboxMat?.shader?.name == "Skybox/Panoramic" && skyboxMat.mainTexture != null)
            {
                var avg = skyboxMat._cachedCubemap?.GetAverageColor() ?? skyboxMat.mainTexture.GetAverageColor();
                float exposure = skyboxMat.exposure;
                return new Vector4(avg.r * exposure * intensity, avg.g * exposure * intensity, avg.b * exposure * intensity, intensity);
            }

            if (skyboxMat != null)
            {
                var c = RenderSettings.ambientLight;
                return new Vector4(c.r * intensity, c.g * intensity, c.b * intensity, intensity);
            }

            var skyR = (SkyZenithColor.X * SkyZenithIntensity + SkyHorizonColor.X * SkyHorizonIntensity) * 0.5f;
            var skyG = (SkyZenithColor.Y * SkyZenithIntensity + SkyHorizonColor.Y * SkyHorizonIntensity) * 0.5f;
            var skyB = (SkyZenithColor.Z * SkyZenithIntensity + SkyHorizonColor.Z * SkyHorizonIntensity) * 0.5f;
            return new Vector4(skyR * intensity, skyG * intensity, skyB * intensity, intensity);
        }

        // ==============================
        // Forward light upload (for sprites/text)
        // ==============================

        private const int MaxForwardLights = 8;

        private void UploadForwardLightData(CommandList cl, Camera camera)
        {
            var camPos = camera.transform.position;
            var lightData = new LightUniforms
            {
                CameraPos = new Vector4(camPos.x, camPos.y, camPos.z, 0),
            };

            int count = 0;
            foreach (var light in Light._allLights)
            {
                if (count >= MaxForwardLights) break;
                if (!light.enabled || !light.gameObject.activeInHierarchy) continue;
                SetLightInfo(ref lightData, count++, CollectLightInfo(light));
            }

            lightData.LightCount = count;
            cl.UpdateBuffer(_lightBuffer, 0, lightData);
        }

        private static unsafe void SetLightInfo(ref LightUniforms data, int index, LightInfoGPU info)
        {
            fixed (LightInfoGPU* ptr = &data.Light0)
                ptr[index] = info;
        }
    }
}
