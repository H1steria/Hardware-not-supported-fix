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
    //  PARCHE 1 — PlayerControllerB.Start
    // ════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(PlayerControllerB), "Start")]
    public class PlayerControllerPatch
    {
        [HarmonyPostfix]
        static void PostfixStart()
        {
            GraphicsReducer.ApplyAll();
            // Usamos la instancia del plugin para iniciar la corrutina de forma segura
            Plugin.Instance.StartCoroutine(DelayedApply());
        }

        static IEnumerator DelayedApply()
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForSeconds(1f);
            GraphicsReducer.ApplyAll();
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  PARCHE 2 — Interceptar cada cámara HDRP al crearse
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
    //  PARCHE 3 — Primer frame real del pipeline
    // ════════════════════════════════════════════════════════════════
    [HarmonyPatch(typeof(HDRenderPipeline), "Render")]
    public class HDRenderPipelinePatch
    {
        static bool firstCall = true;
        [HarmonyPrefix]
        static void PrefixRender()
        {
            if (!firstCall) return;
            firstCall = false;
            GraphicsReducer.ApplyAll();
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
                    s.dynamicResolutionSettings.forcedPercentage = 50f;
                    s.dynamicResolutionSettings.minPercentage    = 50f;
                    s.dynamicResolutionSettings.maxPercentage    = 50f;

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
        public static void ApplyAll()
        {
            DisablePerCameraSettings();
            DisableVolumetricLights();
            DisableVolumeProfiles();
            DisableLocalFogs();
            DisableReflectionProbes();
        }

        // ── 1. FrameSettings por cámara ──────────────────────────
        static void DisablePerCameraSettings()
        {
            foreach (var cam in UnityEngine.Object.FindObjectsOfType<Camera>(true))
                ApplyCameraOverrides(cam);
        }

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

        // ── 2. Luces — sombras y volumétricos a cero ─────────────
        //  Aquí sí podemos deshabilitar sombras de forma segura,
        //  actuando sobre cada luz en vez de sobre el manager global.
        static void DisableVolumetricLights()
        {
            foreach (var light in UnityEngine.Object.FindObjectsOfType<HDAdditionalLightData>(true))
            {
                if (light == null) continue;
                try
                {
                    light.volumetricDimmer       = 0f;
                    light.volumetricShadowDimmer = 0f;
                    light.EnableShadows(false);         // seguro: actúa por luz
                }
                catch { }
            }
        }

        // ── 3. Perfiles de Volume ─────────────────────────────────
        static void DisableVolumeProfiles()
        {
            foreach (var vol in UnityEngine.Object.FindObjectsOfType<Volume>(true))
            {
                if (vol == null) continue;
                VolumeProfile profile = vol.sharedProfile ?? vol.profile;
                if (profile == null) continue;
                try
                {
                    if (profile.TryGet<Fog>(out var fog))
                    {
                        fog.active = false;
                        fog.enabled.Override(false);
                        fog.enableVolumetricFog.Override(false);
                    }
                    if (profile.TryGet<VolumetricClouds>(out var clouds))
                    {
                        clouds.active = false;
                        clouds.enable.Override(false);
                    }
                    if (profile.TryGet<ScreenSpaceReflection>(out var ssr))
                        ssr.active = false;
                    if (profile.TryGet<ScreenSpaceAmbientOcclusion>(out var ssao))
                        ssao.active = false;
                    if (profile.TryGet<MotionBlur>(out var mb))
                        mb.active = false;
                    if (profile.TryGet<Bloom>(out var bloom))
                        bloom.active = false;
                    if (profile.TryGet<DepthOfField>(out var dof))
                        dof.active = false;
                    if (profile.TryGet<ChromaticAberration>(out var ca))
                        ca.active = false;
                    if (profile.TryGet<GlobalIllumination>(out var gi))
                        gi.active = false;
                    if (profile.TryGet<ContactShadows>(out var cs))
                        cs.active = false;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[AntiCrash] Volume '{vol.name}': {ex.Message}");
                }
            }
        }

        // ── 4. Niebla local ───────────────────────────────────────
        static void DisableLocalFogs()
        {
            foreach (var lf in UnityEngine.Object.FindObjectsOfType<LocalVolumetricFog>(true))
                if (lf != null) lf.enabled = false;
        }

        // ── 5. Reflection Probes ──────────────────────────────────
        static void DisableReflectionProbes()
        {
            foreach (var rp in UnityEngine.Object.FindObjectsOfType<ReflectionProbe>(true))
                if (rp != null) rp.enabled = false;

            foreach (var hdRp in UnityEngine.Object.FindObjectsOfType<HDAdditionalReflectionData>(true))
                if (hdRp != null) hdRp.enabled = false;
        }
    }
}