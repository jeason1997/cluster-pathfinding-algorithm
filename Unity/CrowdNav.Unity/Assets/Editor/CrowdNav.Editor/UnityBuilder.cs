using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace CrowdNav.Unity.EditorTools
{
    public static class UnityBuilder
    {
        private const string ScenePath = "Assets/Scenes/CrowdDemo.unity";
        private const string MapPath = "Assets/CrowdNav.App/Resources/neicheng_walk01.json";

        public static void BuildAndroid()
        {
            BuildAndroidInternal(false);
        }

        public static void BuildAndroidBench()
        {
            BuildAndroidInternal(true);
        }

        private static void BuildAndroidInternal(bool benchmark)
        {
            int exit = 2;
            try
            {
                PrepareScene();
                ApplyAndroidSettings();
                PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Android,
                    benchmark ? "CROWD_AUTOBENCH" : "");
                Directory.CreateDirectory("Builds/Android");
                string apk = benchmark ? "Builds/Android/CrowdNavBench.apk" : "Builds/Android/CrowdNavDemo.apk";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = apk,
                    target = BuildTarget.Android,
                    options = BuildOptions.None
                });
                sw.Stop();
                var summary = report.summary;
                Debug.Log(string.Format("BUILD_RESULT {0} apk={1} sizeMB={2:0.0} errors={3} warnings={4} timeSec={5:0.0}",
                    summary.result, apk, summary.totalSize / 1048576.0, summary.totalErrors,
                    summary.totalWarnings, sw.Elapsed.TotalSeconds));
                exit = summary.result == BuildResult.Succeeded ? 0 : 1;
            }
            catch (Exception ex)
            {
                Debug.LogError("BUILD_FAIL " + ex);
                exit = 2;
            }
            finally
            {
                try { PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Android, ""); }
                catch { }
            }
            EditorApplication.Exit(exit);
        }

        [MenuItem("CrowdNav/Prepare Demo Scene")]
        public static void PrepareScene()
        {
            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("CrowdNavDemo");
            var demo = go.AddComponent<CrowdNavDemo>();
            demo.mapAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(MapPath);
            demo.unitCount = 1000;
            demo.targetHz = 30;
            demo.autoStart = true;
            Directory.CreateDirectory("Assets/CrowdNav.App/Materials");
            string unitPath = "Assets/CrowdNav.App/Materials/Unit.mat";
            string groundPath = "Assets/CrowdNav.App/Materials/Ground.mat";
            AssetDatabase.DeleteAsset(unitPath);
            AssetDatabase.DeleteAsset(groundPath);
            var standard = Shader.Find("Standard");
            var unitShader = Shader.Find("CrowdNav/UnitInstanced");
            if (unitShader == null) throw new InvalidOperationException("CrowdNav/UnitInstanced shader not found");
            var unit = new Material(unitShader);
            unit.enableInstancing = true;
            unit.SetColor("_Color", Color.white);
            AssetDatabase.CreateAsset(unit, unitPath);
            var ground = new Material(standard) { color = new Color(0.34f, 0.36f, 0.38f) };
            ground.SetFloat("_Glossiness", 0.05f);
            AssetDatabase.CreateAsset(ground, groundPath);
            AssetDatabase.SaveAssets();
            demo.unitMaterial = AssetDatabase.LoadAssetAtPath<Material>(unitPath);
            demo.groundMaterial = AssetDatabase.LoadAssetAtPath<Material>(groundPath);
            if (demo.mapAsset == null) throw new InvalidOperationException("Map asset missing: " + MapPath);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            Debug.Log("SCENE_READY " + ScenePath);
        }

        [MenuItem("CrowdNav/Apply Android Settings")]
        public static void ApplyAndroidSettings()
        {
            PlayerSettings.companyName = "CrowdNav";
            PlayerSettings.productName = "CrowdNav1000";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.crowdnav.demo");
            PlayerSettings.bundleVersion = "1.0.0";
            PlayerSettings.Android.bundleVersionCode = 1;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.useCustomKeystore = false;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Android, Il2CppCompilerConfiguration.Release);
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Android, ManagedStrippingLevel.Low);
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Android, ApiCompatibilityLevel.NET_Standard);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.colorSpace = ColorSpace.Gamma;
            PlayerSettings.MTRendering = true;
            EditorUserBuildSettings.buildAppBundle = false;
            AssetDatabase.SaveAssets();
            Debug.Log("ANDROID_SETTINGS_READY");
        }
    }
}