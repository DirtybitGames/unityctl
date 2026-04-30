using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UnityCtl.TestProject.Editor
{
    /// <summary>
    /// One-off helper to produce a development+autoconnect Android build for profiling tests.
    /// Use from unityctl: `./uc script eval "UnityCtl.TestProject.Editor.AndroidProfileBuild.Build()"`.
    /// </summary>
    public static class AndroidProfileBuild
    {
        public const string AppIdentifier = "com.dirtybit.unityctl.test";
        public const string ProductName = "unityctl-test";

        public static readonly string MarkerPath =
            Path.Combine(Application.dataPath, "..", "Builds", "Android", "build-status.txt");

        /// <summary>
        /// Schedule the build for the next editor tick and return immediately. The build
        /// can run for many minutes; callers poll <see cref="MarkerPath"/> for status.
        /// </summary>
        [MenuItem("Tools/UnityCtl/Build Android Profile (Dev + AutoConnect)")]
        public static void ScheduleBuild()
        {
            var dir = Path.GetDirectoryName(MarkerPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(MarkerPath, "RUNNING\n");
            Debug.Log("[AndroidProfileBuild] Scheduled — running on next editor tick.");
            EditorApplication.delayCall += () =>
            {
                try
                {
                    var apk = Build();
                    File.WriteAllText(MarkerPath, $"OK\n{apk}\n");
                    Debug.Log($"[AndroidProfileBuild] OK: {apk}");
                }
                catch (Exception ex)
                {
                    File.WriteAllText(MarkerPath, $"FAIL\n{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
                    Debug.LogError($"[AndroidProfileBuild] FAIL: {ex}");
                }
            };
        }

        public static string Build()
        {
            // Identifier + product name — keep separate from other Unity test apps on device.
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, AppIdentifier);
            PlayerSettings.productName = ProductName;
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);

            // Modern Android (arm64-v8a is the only ABI Google Play accepts now).
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            // Enable Development + Autoconnect Profiler globally for the build.
            EditorUserBuildSettings.development = true;
            EditorUserBuildSettings.connectProfiler = true;
            EditorUserBuildSettings.allowDebugging = true;
            EditorUserBuildSettings.buildAppBundle = false;

            var outputDir = Path.Combine(Application.dataPath, "..", "Builds", "Android");
            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, "unityctl-test.apk");

            var options = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/SampleScene.unity" },
                locationPathName = outputPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.Development | BuildOptions.ConnectWithProfiler | BuildOptions.AllowDebugging
            };

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            if (summary.result != BuildResult.Succeeded)
            {
                throw new Exception($"Android build failed: {summary.result} ({summary.totalErrors} errors)");
            }

            return Path.GetFullPath(outputPath);
        }
    }
}
