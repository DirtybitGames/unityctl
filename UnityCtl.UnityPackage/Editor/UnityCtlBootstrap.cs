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
        private static readonly object _compilerMessagesLock = new object();

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
            // Stop any active recording when exiting play mode
            if (stateChange == PlayModeStateChange.ExitingPlayMode)
            {
                Editor.RecordingManager.Instance.StopIfActive();
            }

            UnityCtlClient.Instance.SendPlayModeChangedEvent(stateChange);
        }

        private static void OnCompilationStarted(object obj)
        {
            // Clear collected messages when compilation starts
            lock (_compilerMessagesLock) { _compilerMessages.Clear(); }
            UnityCtlClient.Instance.SendCompilationStartedEvent();
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            // Collect messages from each assembly as it finishes (called from background thread)
            if (messages != null && messages.Length > 0)
            {
                lock (_compilerMessagesLock) { _compilerMessages.AddRange(messages); }
            }
        }

        private static void OnCompilationFinished(object obj)
        {
            // Snapshot under lock, then use copy outside lock
            List<CompilerMessage> messagesCopy;
            lock (_compilerMessagesLock)
            {
                messagesCopy = new List<CompilerMessage>(_compilerMessages);
                _compilerMessages.Clear();
            }

            var hasErrors = messagesCopy.Exists(m => m.type == CompilerMessageType.Error);
            var success = !hasErrors;

            UnityCtlClient.Instance.SendCompilationFinishedEvent(success, messagesCopy);
        }

        private static void OnBeforeAssemblyReload()
        {
            // This fires RIGHT BEFORE domain reload - send notification to bridge
            UnityCtlClient.Instance.SendDomainReloadStartingEvent();
        }
    }
}
