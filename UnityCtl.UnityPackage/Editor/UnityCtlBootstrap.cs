using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityCtl
{
    [InitializeOnLoad]
    public static class UnityCtlBootstrap
    {
        static UnityCtlBootstrap()
        {
            // Subscribe to events
            EditorApplication.update += UnityCtlClient.Instance.Pump;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            // Initial connection attempt
            EditorApplication.delayCall += () =>
            {
                UnityCtlClient.Instance.TryConnectIfBridgePresent();
            };
        }

        [DidReloadScripts]
        private static void OnScriptsReloaded()
        {
            // Reconnect after domain reload
            UnityCtlClient.Instance.TryConnectIfBridgePresent();
        }

        private static void OnLogMessageReceived(string message, string stackTrace, LogType type)
        {
            UnityCtlClient.Instance.SendLogEvent(message, stackTrace, type);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange stateChange)
        {
            UnityCtlClient.Instance.SendPlayModeChangedEvent(stateChange);
        }

        private static void OnCompilationStarted(object obj)
        {
            UnityCtlClient.Instance.SendCompilationStartedEvent();
        }

        private static void OnCompilationFinished(object obj)
        {
            // Check if compilation was successful by checking for compile errors
            var success = !EditorUtility.scriptCompilationFailed;
            UnityCtlClient.Instance.SendCompilationFinishedEvent(success);
        }

        private static void OnBeforeAssemblyReload()
        {
            // This fires RIGHT BEFORE domain reload - send notification to bridge
            UnityCtlClient.Instance.SendDomainReloadStartingEvent();
        }
    }
}
