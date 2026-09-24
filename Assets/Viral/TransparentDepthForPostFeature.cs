using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

/// <summary>
/// Stamps transparent objects and world-space UI into the camera depth texture
/// just before post-processing, so depth of field (and any other post effect that
/// reads scene depth) sees them instead of whatever lies behind. Without this a
/// transparent object or UI panel sticking out over empty space takes the far
/// background's depth and gets blurred with it.
///
/// Only the depth texture post reads is touched: the transparent drawing and
/// the real depth buffer used while rendering are unchanged. Texture alpha below
/// the cutoff doesn't count, so glyphs and sprites stamp their own shapes.
/// </summary>
public class TransparentDepthForPostFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        [Tooltip("Layers stamped into depth. World-space UI is usually on UI. Narrow this if smoke or glass shouldn't count as solid for depth of field.")]
        public LayerMask layerMask = ~0;

        [Range(0f, 1f), Tooltip("Texture alpha below this doesn't stamp (glyph edges, soft sprite borders).")]
        public float alphaCutoff = 0.1f;

        [Tooltip("REQUIRED FOR BUILDS: assign Hidden/TransparentDepthForPost so it isn't stripped.")]
        public Shader shader;

        [Tooltip("Paint everything that gets stamped magenta, to check what the pass picks up.")]
        public bool debugShowStamped;
    }

    public Settings settings = new Settings();
    StampPass _pass;

    static readonly int CutoffId = Shader.PropertyToID("_TransparentDepthCutoff");
    static readonly int UseTextureId = Shader.PropertyToID("_TransparentDepthUseTexture");

    public override void Create()
    {
        Shader shader = settings.shader;
#if UNITY_EDITOR
        if (!shader) shader = Shader.Find("Hidden/TransparentDepthForPost"); // editor convenience only
#endif
        _pass?.Dispose();
        _pass = shader ? new StampPass(shader) { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing } : null;
        if (!shader) Debug.LogError("TransparentDepthForPostFeature: assign Hidden/TransparentDepthForPost to Shader.");
    }

    protected override void Dispose(bool disposing) => _pass?.Dispose();

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (_pass == null) return;
        CameraType type = renderingData.cameraData.cameraType;
        if (type == CameraType.Preview || type == CameraType.Reflection) return;

        Shader.SetGlobalFloat(CutoffId, settings.alphaCutoff);
        _pass.layerMask = settings.layerMask;
        _pass.debug = settings.debugShowStamped;
        renderer.EnqueuePass(_pass);
    }

    class StampPass : ScriptableRenderPass
    {
        public LayerMask layerMask;
        public bool debug;

        // An override material, not an override shader: in this URP version override-shader
        // renderer lists came back drawing nothing. UI and text still get their texture from the
        // canvas; lit transparents see this material's white _MainTex and stamp whole.
        readonly Material _material;

        // Lit transparents stamp solid; untagged passes (UI, TextMeshPro, sprites) cut by texture alpha.
        static readonly ShaderTagId[] LitTags = { new ShaderTagId("UniversalForward"), new ShaderTagId("UniversalForwardOnly") };
        static readonly ShaderTagId[] UnlitTags = { new ShaderTagId("SRPDefaultUnlit") };
        static bool s_logged;

        class PassData { public RendererListHandle lit, unlit; }

        public StampPass(Shader shader)
        {
            _material = CoreUtils.CreateEngineMaterial(shader);
            ConfigureInput(ScriptableRenderPassInput.Depth); // make sure the depth texture exists
        }

        public void Dispose() => CoreUtils.Destroy(_material);

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameContext)
        {
            var resources = frameContext.Get<UniversalResourceData>();
            TextureHandle depth = resources.cameraDepthTexture;
            if (!depth.IsValid()) return;

            // URP keeps the depth texture either as a real depth buffer or as an R32 colour
            // copy, depending on platform and setup. Stamp it whichever way it is.
            TextureDesc desc = renderGraph.GetTextureDesc(depth);
            bool isDepthBuffer = desc.depthBufferBits != DepthBits.None;
            int shaderPass = isDepthBuffer ? 0 : SystemInfo.usesReversedZBuffer ? 1 : 2;

            if (!s_logged)
            {
                s_logged = true;
                Debug.Log($"TransparentDepthForPost: stamping into the depth texture as a {(isDepthBuffer ? "depth buffer" : "R32 colour copy")} ({desc.colorFormat}, {desc.depthBufferBits}), shader pass {shaderPass}.");
            }

            var renderingData = frameContext.Get<UniversalRenderingData>();
            var cameraData = frameContext.Get<UniversalCameraData>();
            var lightData = frameContext.Get<UniversalLightData>();

            // Transparent range, excluding Overlay (full-screen effect quads live there).
            var filtering = new FilteringSettings(new RenderQueueRange(2501, 3999), layerMask);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Transparent Depth For Post", out var data))
            {
                data.lit = renderGraph.CreateRendererList(List(LitTags, renderingData, cameraData, lightData, filtering, shaderPass));
                data.unlit = renderGraph.CreateRendererList(List(UnlitTags, renderingData, cameraData, lightData, filtering, shaderPass));
                builder.UseRendererList(data.lit);
                builder.UseRendererList(data.unlit);

                if (isDepthBuffer) builder.SetRenderAttachmentDepth(depth, AccessFlags.ReadWrite);
                else builder.SetRenderAttachment(depth, 0, AccessFlags.ReadWrite);

                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
                {
                    ctx.cmd.SetGlobalFloat(UseTextureId, 0f);
                    ctx.cmd.DrawRendererList(d.lit);
                    ctx.cmd.SetGlobalFloat(UseTextureId, 1f);
                    ctx.cmd.DrawRendererList(d.unlit);
                });
            }

            if (!debug || !resources.activeColorTexture.IsValid()) return;
            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Transparent Depth For Post (Debug)", out var data))
            {
                data.lit = renderGraph.CreateRendererList(List(LitTags, renderingData, cameraData, lightData, filtering, 3));
                data.unlit = renderGraph.CreateRendererList(List(UnlitTags, renderingData, cameraData, lightData, filtering, 3));
                builder.UseRendererList(data.lit);
                builder.UseRendererList(data.unlit);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
                {
                    ctx.cmd.SetGlobalFloat(UseTextureId, 0f);
                    ctx.cmd.DrawRendererList(d.lit);
                    ctx.cmd.SetGlobalFloat(UseTextureId, 1f);
                    ctx.cmd.DrawRendererList(d.unlit);
                });
            }
        }

        RendererListParams List(ShaderTagId[] tags, UniversalRenderingData renderingData, UniversalCameraData cameraData,
                                UniversalLightData lightData, FilteringSettings filtering, int shaderPass)
        {
            DrawingSettings drawing = RenderingUtils.CreateDrawingSettings(tags[0], renderingData, cameraData, lightData, SortingCriteria.CommonTransparent);
            for (int i = 1; i < tags.Length; i++) drawing.SetShaderPassName(i, tags[i]);
            drawing.overrideMaterial = _material;
            drawing.overrideMaterialPassIndex = shaderPass;
            return new RendererListParams(renderingData.cullResults, drawing, filtering);
        }
    }
}
