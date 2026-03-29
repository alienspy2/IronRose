// ------------------------------------------------------------
// @file    RenderSystem.Draw.cs
// @brief   메시, 스프라이트, 텍스트의 실제 드로우 메서드를 담당하는 RenderSystem partial 클래스.
//          불투명/반투명 메시 분리 렌더링, 스프라이트/텍스트 정렬 렌더링을 수행한다.
// @deps    RenderSystem (partial), MeshRenderer, MeshFilter, SpriteRenderer, TextRenderer,
//          Camera, Material, BlendMode, MaterialUniforms
// @exports
//   partial class RenderSystem
//     DrawOpaqueRenderers(cl, viewProj): void                 — BlendMode.Opaque 메시만 렌더링
//     DrawTransparentRenderers(cl, viewProj, camera): void    — AlphaBlend/Additive 메시를 Back-to-Front 정렬 후 렌더링
//     DrawAllRenderers(cl, viewProj, useWireframeColor): void — 모든 메시 렌더링 (와이어프레임용)
//     DrawAllSprites(cl, viewProj, camera): void              — 스프라이트를 정렬 후 렌더링
//     DrawAllTexts(cl, viewProj, camera): void                — 텍스트를 정렬 후 렌더링
// @note    DrawTransparentRenderers는 내부에서 UploadForwardLightData를 호출하므로 독립적으로 사용 가능.
//          매 프레임 List 할당이 발생하므로 향후 최적화 대상.
// ------------------------------------------------------------
using System.Linq;
using System.Numerics;
using Veldrid;
using RoseEngine;
using Vector4 = System.Numerics.Vector4;

namespace IronRose.Rendering
{
    // Mesh, sprite, and text draw methods.
    // Extracted from RenderSystem (Phase 15 — H-1).
    public partial class RenderSystem
    {
        private void DrawOpaqueRenderers(CommandList cl, System.Numerics.Matrix4x4 viewProj)
        {
            foreach (var renderer in MeshRenderer._allRenderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer.gameObject._isEditorInternal) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter?.mesh == null) continue;

                // Material override: drag-hover preview용 임시 Material
                var mat = (_materialOverride != null &&
                           renderer.gameObject.GetInstanceID() == _materialOverrideObjectId)
                    ? _materialOverride
                    : renderer.material;

                // Opaque가 아닌 메시는 Forward transparent 패스에서 처리
                if ((mat?.blendMode ?? BlendMode.Opaque) != BlendMode.Opaque) continue;

                var mesh = filter.mesh;
                mesh.UploadToGPU(_device!);
                if (mesh.VertexBuffer == null || mesh.IndexBuffer == null) continue;

                var (matUniforms, texView, normalTexView, mroTexView) = PrepareMaterial(mat);
                DrawMesh(cl, viewProj, mesh, renderer.transform, matUniforms, texView, bindPerFrame: false, normalTexView, mroTexView);
            }
        }

        private void DrawTransparentRenderers(CommandList cl, System.Numerics.Matrix4x4 viewProj, Camera camera)
        {
            // 1. 반투명 메시 수집
            var transparentList = new System.Collections.Generic.List<(MeshRenderer renderer, Material mat, float distSq)>();
            var camPos = camera.transform.position;

            foreach (var renderer in MeshRenderer._allRenderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer.gameObject._isEditorInternal) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter?.mesh == null) continue;

                var mat = (_materialOverride != null &&
                           renderer.gameObject.GetInstanceID() == _materialOverrideObjectId)
                    ? _materialOverride
                    : renderer.material;

                var blendMode = mat?.blendMode ?? BlendMode.Opaque;
                if (blendMode == BlendMode.Opaque) continue;

                float distSq = (renderer.transform.position - camPos).sqrMagnitude;
                transparentList.Add((renderer, mat!, distSq));
            }

            if (transparentList.Count == 0) return;

            // 2. 카메라에서 먼 순서(Back-to-Front)로 정렬
            transparentList.Sort((a, b) => b.distSq.CompareTo(a.distSq));

            // 3. Forward 라이트 데이터 업로드
            UploadForwardLightData(cl, camera);

            // 4. 블렌드 모드별로 파이프라인 바인딩하며 그리기
            BlendMode currentMode = BlendMode.Opaque; // sentinel (아직 파이프라인 미바인딩)

            foreach (var (renderer, mat, _) in transparentList)
            {
                if (mat.blendMode != currentMode)
                {
                    currentMode = mat.blendMode;
                    cl.SetPipeline(currentMode == BlendMode.AlphaBlend
                        ? _meshAlphaBlendPipeline
                        : _meshAdditivePipeline);
                }

                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = filter!.mesh!;
                mesh.UploadToGPU(_device!);
                if (mesh.VertexBuffer == null || mesh.IndexBuffer == null) continue;

                var (matUniforms, texView, normalTexView, mroTexView) = PrepareMaterial(mat);
                DrawMesh(cl, viewProj, mesh, renderer.transform, matUniforms, texView,
                         bindPerFrame: true, normalTexView, mroTexView);
            }
        }

        private void DrawAllRenderers(CommandList cl, System.Numerics.Matrix4x4 viewProj, bool useWireframeColor)
        {
            foreach (var renderer in MeshRenderer._allRenderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) continue;
                if (renderer.gameObject._isEditorInternal) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter?.mesh == null) continue;
                var mesh = filter.mesh;
                mesh.UploadToGPU(_device!);
                if (mesh.VertexBuffer == null || mesh.IndexBuffer == null) continue;

                MaterialUniforms matUniforms;
                TextureView? texView;
                TextureView? normalTexView = null;
                TextureView? mroTexView = null;
                if (useWireframeColor)
                {
                    var wc = DebugOverlaySettings.wireframeColor;
                    matUniforms = new MaterialUniforms
                    {
                        Color = new Vector4(wc.r, wc.g, wc.b, wc.a),
                        Roughness = 0.5f, Occlusion = 1f,
                    };
                    texView = null;
                }
                else
                {
                    (matUniforms, texView, normalTexView, mroTexView) = PrepareMaterial(renderer.material);
                }

                DrawMesh(cl, viewProj, mesh, renderer.transform, matUniforms, texView, bindPerFrame: true, normalTexView, mroTexView);
            }
        }

        private void SetUnlitLightData(CommandList cl, Camera camera)
        {
            cl.UpdateBuffer(_lightBuffer, 0, new LightUniforms
            {
                CameraPos = new Vector4(camera.transform.position.x, camera.transform.position.y, camera.transform.position.z, 0),
                LightCount = -1,
            });
        }

        private void DrawAllSprites(CommandList cl, System.Numerics.Matrix4x4 viewProj, Camera camera)
        {
            SetUnlitLightData(cl, camera);
            cl.SetPipeline(_spritePipeline);

            var active = SpriteRenderer._allSpriteRenderers
                .Where(sr => sr.enabled && sr.sprite != null &&
                             sr.gameObject.activeInHierarchy && !sr._isDestroyed)
                .ToList();
            if (active.Count == 0) return;

            var camPos = camera.transform.position;
            active.Sort((a, b) =>
            {
                int orderCmp = a.sortingOrder.CompareTo(b.sortingOrder);
                if (orderCmp != 0) return orderCmp;
                return (b.transform.position - camPos).sqrMagnitude
                    .CompareTo((a.transform.position - camPos).sqrMagnitude);
            });

            foreach (var sr in active)
            {
                sr.EnsureMesh();
                if (sr._cachedMesh == null) continue;
                var mesh = sr._cachedMesh;
                mesh.UploadToGPU(_device!);
                if (mesh.VertexBuffer == null || mesh.IndexBuffer == null) continue;

                TextureView? texView = null;
                float hasTexture = 0f;
                sr.sprite!.texture.UploadToGPU(_device!);
                if (sr.sprite.texture.TextureView != null)
                { texView = sr.sprite.texture.TextureView; hasTexture = 1f; }

                var c = sr.color;
                DrawMesh(cl, viewProj, mesh, sr.transform, new MaterialUniforms
                {
                    Color = new Vector4(c.r, c.g, c.b, c.a),
                    HasTexture = hasTexture,
                }, texView, bindPerFrame: true);
            }
        }

        private void DrawAllTexts(CommandList cl, System.Numerics.Matrix4x4 viewProj, Camera camera)
        {
            SetUnlitLightData(cl, camera);
            cl.SetPipeline(_spritePipeline);

            var active = TextRenderer._allTextRenderers
                .Where(tr => tr.enabled && tr.font?.atlasTexture != null &&
                             !string.IsNullOrEmpty(tr.text) &&
                             tr.gameObject.activeInHierarchy && !tr._isDestroyed)
                .ToList();
            if (active.Count == 0) return;

            var camPos = camera.transform.position;
            active.Sort((a, b) =>
            {
                int orderCmp = a.sortingOrder.CompareTo(b.sortingOrder);
                if (orderCmp != 0) return orderCmp;
                return (b.transform.position - camPos).sqrMagnitude
                    .CompareTo((a.transform.position - camPos).sqrMagnitude);
            });

            foreach (var tr in active)
            {
                tr.EnsureMesh();
                if (tr._cachedMesh == null) continue;
                var mesh = tr._cachedMesh;
                mesh.UploadToGPU(_device!);
                if (mesh.VertexBuffer == null || mesh.IndexBuffer == null) continue;

                TextureView? texView = null;
                float hasTexture = 0f;
                tr.font!.atlasTexture!.UploadToGPU(_device!);
                if (tr.font.atlasTexture.TextureView != null)
                { texView = tr.font.atlasTexture.TextureView; hasTexture = 1f; }

                DrawMesh(cl, viewProj, mesh, tr.transform, new MaterialUniforms
                {
                    Color = new Vector4(tr.color.r, tr.color.g, tr.color.b, tr.color.a),
                    HasTexture = hasTexture,
                }, texView, bindPerFrame: true);
            }
        }
    }
}
