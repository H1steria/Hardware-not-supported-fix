using System;
using System.Collections;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using GameNetcodeStuff;

namespace AntiCrashVolumetrics
{
    [BepInPlugin("com.usuario.anticrashfog", "Fix Grafico Extremo", "3.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        private readonly Harmony harmony = new Harmony("com.usuario.anticrashfog");
        public static Plugin Instance { get; private set; }

        void Awake()
        {
            Instance = this;
            Logger.LogInfo("[AntiCrash] v3.0 cargado.");
            GraphicsReducer.ApplySystemLevel();
            harmony.PatchAll();
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Interceptar cada cámara HDRP al crearse
    // ════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(HDAdditionalCameraData), "Awake")]
    public class HDCameraAwakePatch
    {
        [HarmonyPostfix]
        static void PostfixAwake(HDAdditionalCameraData __instance)
        {
            var cam = __instance.GetComponent<Camera>();
            if (cam != null)
                GraphicsReducer.ApplyCameraOverrides(cam);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  LÓGICA CENTRAL
    // ════════════════════════════════════════════════════════════════
    public static class GraphicsReducer
    {
        // ─────────────────────────────────────────────────────────
        //  IMPORTANTE: NO incluir campos que rompan managers internos:
        //
        //  ✗ ShadowMaps          → rompe HDShadowManager (NullRef en atlas)
        //  ✗ ContactShadows      → depende del shadow manager activo
        //  ✗ ScreenSpaceShadows  → mismo problema
        //
        //  Las sombras se desactivan SOLO a nivel de luz individual.
        // ─────────────────────────────────────────────────────────
        static readonly FrameSettingsField[] DisabledFields =
        {
            // Volumétricos — causa original del crash de hardware
            FrameSettingsField.Volumetrics,
            FrameSettingsField.AtmosphericScattering,
            FrameSettingsField.VolumetricClouds,

            // Ray tracing y reflexiones costosas
            FrameSettingsField.SSR,
            FrameSettingsField.SSAO,
            FrameSettingsField.SSGI,
            FrameSettingsField.RayTracing,
            FrameSettingsField.TransparentSSR,

            // Post-proceso pesado
            FrameSettingsField.MotionBlur,
            FrameSettingsField.Bloom,
            FrameSettingsField.DepthOfField,
            FrameSettingsField.ChromaticAberration,
            FrameSettingsField.LensFlareDataDriven,

            // Iluminación avanzada
            FrameSettingsField.ProbeVolume,
            FrameSettingsField.ReflectionProbe,
            FrameSettingsField.PlanarProbe,
            FrameSettingsField.SubsurfaceScattering,
            FrameSettingsField.Transmission,

            // Transparencias y decals
            FrameSettingsField.TransparentPrepass,
            FrameSettingsField.TransparentPostpass,
            FrameSettingsField.Decals,
            FrameSettingsField.DecalLayers,
        };

        // ─────────────────────────────────────────────────────────
        //  Nivel sistema — llamado en Awake del plugin
        // ─────────────────────────────────────────────────────────
        public static void ApplySystemLevel()
        {
            try
            {
                QualitySettings.SetQualityLevel(0, true);
                QualitySettings.globalTextureMipmapLimit = 3;
                QualitySettings.maximumLODLevel          = 2;
                QualitySettings.lodBias                  = 0.3f;
                QualitySettings.anisotropicFiltering     = AnisotropicFiltering.Disable;
                QualitySettings.vSyncCount               = 0;
                Application.targetFrameRate              = 30;

                // ── Sombras del sistema: mínimo pero SIN poner cero ──
                // QualitySettings.shadows = Disable haría que Unity las
                // saltee completamente pero HDRP gestiona sus propias
                // sombras por separado; dejamos el sistema en Low.
                QualitySettings.shadows          = ShadowQuality.HardOnly;
                QualitySettings.shadowDistance   = 15f;   // mínimo útil
                QualitySettings.shadowResolution = ShadowResolution.Low;
                QualitySettings.shadowCascades   = 1;     // mínimo: 1 (no 0)

                var asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
                if (asset != null)
                {
                    var s = asset.currentPlatformRenderPipelineSettings;

                    // ── Volumétricos: interruptor raíz en el asset ──
                    s.supportVolumetrics   = false;
                    s.supportDistortion    = false;

                    // ── Ray tracing ──
                    s.supportRayTracing    = false;

                    // ── Decals ──
                    s.supportDecals        = false;

                    // ── Shader mode: Forward es más ligero que Deferred ──
                    s.supportedLitShaderMode = RenderPipelineSettings.SupportedLitShaderMode.ForwardOnly;

                    // ── Sombras en asset: valores mínimos VÁLIDOS ──
                    // No tocar maxShadowRequests ni poner a 0:
                    // HDShadowManager inicializa el atlas con este valor
                    // y si es 0 explota en InvalidateAtlasOutputsIfNeeded.
                    s.hdShadowInitParams.punctualLightShadowAtlas.shadowAtlasResolution = 256;
                    s.hdShadowInitParams.areaLightShadowAtlas.shadowAtlasResolution     = 256;
                    // maxShadowRequests: dejar el valor por defecto del asset

                    s.hdShadowInitParams.supportScreenSpaceShadows = false;

                    // ── Resolución dinámica al 50% ──
                    s.dynamicResolutionSettings.enabled          = true;
                    s.dynamicResolutionSettings.forceResolution  = true;
                    // s.dynamicResolutionSettings.forcedPercentage = 50f;
                    // s.dynamicResolutionSettings.minPercentage    = 50f;
                    // s.dynamicResolutionSettings.maxPercentage    = 50f;

                    asset.currentPlatformRenderPipelineSettings = s;
                    Debug.Log("[AntiCrash] HDRenderPipelineAsset configurado correctamente.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AntiCrash] ApplySystemLevel: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────
        //  Aplicación completa sobre la escena activa
        // ─────────────────────────────────────────────────────────
        public static void ApplyCameraOverrides(Camera cam)
        {
            if (cam == null) return;
            try
            {
                var hd = cam.GetComponent<HDAdditionalCameraData>()
                         ?? cam.gameObject.AddComponent<HDAdditionalCameraData>();

                hd.customRenderingSettings = true;
                hd.allowDynamicResolution  = true;

                FrameSettings fs         = hd.renderingPathCustomFrameSettings;
                FrameSettingsOverrideMask mask = hd.renderingPathCustomFrameSettingsOverrideMask;

                foreach (var field in DisabledFields)
                {
                    try
                    {
                        fs.SetEnabled(field, false);
                        mask.mask[(uint)field] = true;
                    }
                    catch { /* field no disponible en esta versión de HDRP */ }
                }

                hd.renderingPathCustomFrameSettings             = fs;
                hd.renderingPathCustomFrameSettingsOverrideMask = mask;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AntiCrash] Cámara '{cam.name}': {ex.Message}");
            }
        }
    }
}