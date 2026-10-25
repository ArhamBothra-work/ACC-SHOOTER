using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

public class Water_Volume : ScriptableRendererFeature
{
    class CustomRenderPass : ScriptableRenderPass
    {
        private Material _material;

        public CustomRenderPass(Material mat)
        {
            _material = mat;
        }

        // Render Graph entry point (replaces OnCameraSetup + Execute).
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var cameraData = frameData.Get<UniversalCameraData>();
            if (cameraData.cameraType == CameraType.Reflection)
                return;

            var resourceData = frameData.Get<UniversalResourceData>();

            // Can't read from and write to the back buffer directly; skip if that's the active target.
            if (resourceData.isActiveTargetBackBuffer)
                return;

            TextureHandle source = resourceData.activeColorTexture;

            var descriptor = renderGraph.GetTextureDesc(source);
            descriptor.name = "_TemporaryColourTexture";
            descriptor.clearBuffer = false;

            TextureHandle destination = renderGraph.CreateTexture(descriptor);

            // Apply the water material while blitting source -> temp,
            // then write the result back as the camera's color texture.
            RenderGraphUtils.BlitMaterialParameters blitParams = new(source, destination, _material, 0);
            renderGraph.AddBlitPass(blitParams, "Water_Volume Blit");

            resourceData.cameraColor = destination;
        }
    }

    [System.Serializable]
    public class _Settings
    {
        public Material material = null;
        public RenderPassEvent renderPass = RenderPassEvent.AfterRenderingSkybox;
    }

    public _Settings settings = new _Settings();

    CustomRenderPass m_ScriptablePass;

    public override void Create()
    {
        if (settings.material == null)
        {
            settings.material = (Material)Resources.Load("Water_Volume");
        }

        m_ScriptablePass = new CustomRenderPass(settings.material);
        m_ScriptablePass.renderPassEvent = settings.renderPass;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(m_ScriptablePass);
    }
}