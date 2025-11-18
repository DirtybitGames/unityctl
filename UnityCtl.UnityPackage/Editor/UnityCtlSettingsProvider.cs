using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCtl.Editor
{
    /// <summary>
    /// Provides the UnityCtl settings UI in Unity's Project Settings window.
    /// Access via Edit > Project Settings > UnityCtl
    /// </summary>
    public class UnityCtlSettingsProvider : SettingsProvider
    {
        private const string SettingsPath = "Project/UnityCtl";

        public UnityCtlSettingsProvider(string path, SettingsScope scope = SettingsScope.Project)
            : base(path, scope)
        {
        }

        public override void OnGUI(string searchContext)
        {
            var settings = UnityCtlSettings.Instance;

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Debug Settings", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            var newValue = EditorGUILayout.Toggle(
                new GUIContent(
                    "Show Debug Messages",
                    "Enable internal UnityCtl diagnostic messages in the Unity console (messages prefixed with [UnityCtl])"
                ),
                settings.ShowDebugMessages
            );

            if (EditorGUI.EndChangeCheck())
            {
                settings.ShowDebugMessages = newValue;
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox(
                "When enabled, UnityCtl will print diagnostic messages to the console, such as connection status and internal operations.",
                MessageType.Info
            );
        }

        [SettingsProvider]
        public static SettingsProvider CreateUnityCtlSettingsProvider()
        {
            var provider = new UnityCtlSettingsProvider(SettingsPath, SettingsScope.Project)
            {
                keywords = new System.Collections.Generic.HashSet<string>(new[] { "UnityCtl", "Debug", "Messages", "Console", "Logging" })
            };

            return provider;
        }
    }
}
