using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityCtl
{
    [InitializeOnLoad]
    public static class UnityCtlBootstrap
    {
        // Collect compiler messages across all assemblies during compilation
        private static List<CompilerMessage> _compilerMessages = new List<CompilerMessage>();

        static UnityCtlBootstrap()
        {
            // Subscribe to events
            EditorApplication.update += UnityCtlClient.Instance.Pump;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
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
            // Clear collected messages when compilation starts
            _compilerMessages.Clear();
            UnityCtlClient.Instance.SendCompilationStartedEvent();
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            // Collect messages from each assembly as it finishes
            if (messages != null && messages.Length > 0)
            {
                _compilerMessages.AddRange(messages);
            }
        }

        private static void OnCompilationFinished(object obj)
        {
            // Derive success from collected messages - EditorUtility.scriptCompilationFailed can be unreliable
            var hasErrors = _compilerMessages.Exists(m => m.type == CompilerMessageType.Error);
            var success = !hasErrors;

            // Send compilation finished with collected messages
            UnityCtlClient.Instance.SendCompilationFinishedEvent(success, _compilerMessages);

            // Clear for next compilation
            _compilerMessages.Clear();
        }

        private static void OnBeforeAssemblyReload()
        {
            // This fires RIGHT BEFORE domain reload - send notification to bridge
            UnityCtlClient.Instance.SendDomainReloadStartingEvent();
        }
    }
}
