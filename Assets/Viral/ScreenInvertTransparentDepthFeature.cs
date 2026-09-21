using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Experimental.Rendering;

/// <summary>
/// Captures visible transparent geometry into a separate single-channel
/// normalized-linear-depth texture before URP draws the transparent color pass.
///
/// Why:
/// Standard transparent materials normally do not write to _CameraDepthTexture.
/// ScreenInvertSweep therefore cannot find their silhouettes from normal scene
/// depth. This feature gives the effect a separate transparent-depth texture
/// without polluting the camera's real depth buffer or breaking transparency.
///
/// Unity 6 / URP RenderGraph implementation.
/// </summary>
public class ScreenInvertTransparentDepthFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        [Tooltip("Only these layers are captured. Leave Everything unless you want to restrict it.")]
        public LayerMask layerMask = ~0;

        [Range(2501, 3999)]
        [Tooltip("Lowest render queue captured. 2501 is the start of Unity's transparent range.")]
        public int minimumQueue = 2501;

        [Range(2501, 3999)]
        [Tooltip("Highest queue captured. Default 3999 intentionally excludes Overlay queue objects such as the screen-invert quad itself.")]
        public int maximumQueue = 3999;

        [Tooltip("This must run before URP draws transparent objects.")]
        public RenderPassEvent passEvent =
            RenderPassEvent.BeforeRenderingTransparents;

        [Header("Build-Safe Shader References")]
        [Tooltip("REQUIRED FOR BUILDS. Assign Hidden/ScreenInvertTransparentLinearDepth here so Unity cannot strip it.")]
        public Shader transparentDepthShader;

        [Tooltip("Assign Custom/ScreenInvertSweep here as a build reference too. The feature does not render with it directly, but the serialized reference prevents build stripping if your sweep material is created at runtime.")]
        public Shader screenInvertSweepShader;
    }

    public Settings settings = new Settings();

    Material _depthMaterial;
    TransparentDepthPass _pass;

    static readonly int AvailableID =
        Shader.PropertyToID("_TransparentDepthAvailable");

    public override void Create()
    {
        CoreUtils.Destroy(
            _depthMaterial);

        _depthMaterial = null;
        _pass = null;

        Shader.SetGlobalFloat(
            AvailableID,
            0f);

        Shader shader =
            settings.transparentDepthShader;

#if UNITY_EDITOR
        // Editor convenience only. The serialized field is what makes the
        // player build reliable. Shader.Find by itself is not a build-safe
        // dependency because Unity may strip a shader referenced only by name.
        if (!shader)
        {
            shader =
                Shader.Find(
                    "Hidden/ScreenInvertTransparentLinearDepth");
        }
#endif

        if (!shader)
        {
            Debug.LogError(
                "ScreenInvertTransparentDepthFeature: " +
                "Transparent Depth Shader is not assigned. " +
                "Assign Hidden/ScreenInvertTransparentLinearDepth in the " +
                "Renderer Feature settings. This reference is required so " +
                "the shader is included in player builds.");

            return;
        }

        if (!settings.screenInvertSweepShader)
        {
            Debug.LogWarning(
                "ScreenInvertTransparentDepthFeature: " +
                "Screen Invert Sweep Shader is not assigned. If the sweep " +
                "shader/material is created only at runtime, Unity may strip " +
                "Custom/ScreenInvertSweep from a player build. Assign it in " +
                "this Renderer Feature to guarantee inclusion.");
        }

        _depthMaterial =
            CoreUtils.CreateEngineMaterial(shader);

        _pass =
            new TransparentDepthPass(
                _depthMaterial);

        _pass.renderPassEvent =
            settings.passEvent;
    }

    public override void AddRenderPasses(
        ScriptableRenderer renderer,
        ref RenderingData renderingData)
    {
        if (_pass == null ||
            _depthMaterial == null)
        {
            Shader.SetGlobalFloat(
                AvailableID,
                0f);

            return;
        }

        int minQueue =
            Mathf.Clamp(
                settings.minimumQueue,
                2501,
                3999);

        int maxQueue =
            Mathf.Clamp(
                settings.maximumQueue,
                minQueue,
                3999);

        _pass.Setup(
            settings.layerMask,
            minQueue,
            maxQueue);

        Shader.SetGlobalFloat(
            AvailableID,
            1f);

        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(
        bool disposing)
    {
        Shader.SetGlobalFloat(
            AvailableID,
            0f);

        CoreUtils.Destroy(
            _depthMaterial);
    }

    class TransparentDepthPass :
        ScriptableRenderPass
    {
        readonly Material _overrideMaterial;

        LayerMask _layerMask;
        int _minimumQueue;
        int _maximumQueue;

        static readonly int TextureID =
            Shader.PropertyToID(
                "_TransparentSceneDepthTexture");

        static readonly ShaderTagId UniversalForwardTag =
            new ShaderTagId("UniversalForward");

        static readonly ShaderTagId UniversalForwardOnlyTag =
            new ShaderTagId("UniversalForwardOnly");

        static readonly ShaderTagId SRPDefaultUnlitTag =
            new ShaderTagId("SRPDefaultUnlit");

        class PassData
        {
            public RendererListHandle rendererList;
        }

        public TransparentDepthPass(
            Material overrideMaterial)
        {
            _overrideMaterial =
                overrideMaterial;

            // ScreenInvertSweep samples _CameraDepthTexture. In the Editor
            // some other view/effect may incidentally force depth creation,
            // while a clean player build may not. Explicitly request it.
            ConfigureInput(
                ScriptableRenderPassInput.Depth);
        }

        public void Setup(
            LayerMask layerMask,
            int minimumQueue,
            int maximumQueue)
        {
            _layerMask =
                layerMask;

            _minimumQueue =
                minimumQueue;

            _maximumQueue =
                maximumQueue;
        }

        public override void RecordRenderGraph(
            RenderGraph renderGraph,
            ContextContainer frameContext)
        {
            UniversalRenderingData renderingData =
                frameContext.Get<UniversalRenderingData>();

            UniversalCameraData cameraData =
                frameContext.Get<UniversalCameraData>();

            UniversalLightData lightData =
                frameContext.Get<UniversalLightData>();

            UniversalResourceData resourceData =
                frameContext.Get<UniversalResourceData>();

            RenderTextureDescriptor descriptor =
                cameraData.cameraTargetDescriptor;

            descriptor.msaaSamples = 1;
            descriptor.depthBufferBits = 0;
            descriptor.graphicsFormat =
                GraphicsFormat.R16_SFloat;

            TextureHandle transparentDepth =
                UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph,
                    descriptor,
                    "_TransparentSceneDepthTexture",
                    false);

            RenderQueueRange queueRange =
                new RenderQueueRange
                {
                    lowerBound = _minimumQueue,
                    upperBound = _maximumQueue
                };

            FilteringSettings filteringSettings =
                new FilteringSettings(
                    queueRange,
                    _layerMask);

            DrawingSettings drawingSettings =
                RenderingUtils.CreateDrawingSettings(
                    UniversalForwardTag,
                    renderingData,
                    cameraData,
                    lightData,
                    SortingCriteria.CommonTransparent);

            drawingSettings.SetShaderPassName(
                1,
                UniversalForwardOnlyTag);

            drawingSettings.SetShaderPassName(
                2,
                SRPDefaultUnlitTag);

            drawingSettings.overrideMaterial =
                _overrideMaterial;

            drawingSettings.overrideMaterialPassIndex =
                0;

            RendererListParams listParams =
                new RendererListParams(
                    renderingData.cullResults,
                    drawingSettings,
                    filteringSettings);

            using (
                var builder =
                    renderGraph.AddRasterRenderPass<PassData>(
                        "Screen Invert - Transparent Depth",
                        out var passData))
            {
                passData.rendererList =
                    renderGraph.CreateRendererList(
                        listParams);

                builder.UseRendererList(
                    passData.rendererList);

                builder.SetRenderAttachment(
                    transparentDepth,
                    0,
                    AccessFlags.Write);

                // Test transparent geometry against opaque scene depth without
                // writing to the camera's real depth buffer.
                builder.SetRenderAttachmentDepth(
                    resourceData.activeDepthTexture,
                    AccessFlags.Read);

                builder.SetGlobalTextureAfterPass(
                    transparentDepth,
                    TextureID);

                // The global texture is consumed later by a scene material.
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(
                    static (
                        PassData data,
                        RasterGraphContext context) =>
                    {
                        // 1.0 = far/no transparent geometry.
                        // Do NOT clear the camera depth attachment.
                        context.cmd.ClearRenderTarget(
                            false,
                            true,
                            Color.white);

                        context.cmd.DrawRendererList(
                            data.rendererList);
                    });
            }
        }
    }
}
