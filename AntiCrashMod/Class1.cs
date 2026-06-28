using System;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace AntiCrashVolumetrics
{
    [BepInPlugin("com.usuario.anticrashfog", "Fix Grafico", "3.0.0")]
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
        static readonly FrameSettingsField[] DisabledFields =
        {
            FrameSettingsField.DepthOfField
        };

        // ─────────────────────────────────────────────────────────
        //  Nivel sistema — llamado en Awake del plugin
        // ─────────────────────────────────────────────────────────
        public static void ApplySystemLevel()
        {
            try
            {
                var asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
                if (asset != null)
                {
                    var s = asset.currentPlatformRenderPipelineSettings;

                    s.supportVolumetrics   = false;
                    s.dynamicResolutionSettings.enabled          = true;
                    s.dynamicResolutionSettings.forceResolution  = true;

                    asset.currentPlatformRenderPipelineSettings = s;
                    Debug.Log("[AntiCrash] HDRenderPipelineAsset configurado correctamente.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AntiCrash] ApplySystemLevel: {ex.Message}");
            }
        }


        public static void ApplyCameraOverrides(Camera cam)
        {
            if (cam == null) return;
            try
            {
                var hd = cam.GetComponent<HDAdditionalCameraData>()
                         ?? cam.gameObject.AddComponent<HDAdditionalCameraData>();

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