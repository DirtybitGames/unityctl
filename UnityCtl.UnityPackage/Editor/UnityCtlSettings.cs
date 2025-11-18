using System.IO;
using UnityEngine;

namespace UnityCtl.Editor
{
    /// <summary>
    /// Settings for the UnityCtl editor plugin.
    /// Stored in ProjectSettings/UnityCtlSettings.json
    /// </summary>
    [System.Serializable]
    public class UnityCtlSettings
    {
        private const string SettingsPath = "ProjectSettings/UnityCtlSettings.json";

        public bool showDebugMessages = false;

        public bool ShowDebugMessages
        {
            get => showDebugMessages;
            set
            {
                if (showDebugMessages != value)
                {
                    showDebugMessages = value;
                    Save();
                }
            }
        }

        private static UnityCtlSettings instance;

        /// <summary>
        /// Gets the singleton instance of UnityCtlSettings.
        /// Creates a new instance if one doesn't exist.
        /// </summary>
        public static UnityCtlSettings Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = LoadOrCreateSettings();
                }
                return instance;
            }
        }

        private static UnityCtlSettings LoadOrCreateSettings()
        {
            UnityCtlSettings settings;

            if (File.Exists(SettingsPath))
            {
                try
                {
                    var json = File.ReadAllText(SettingsPath);
                    settings = JsonUtility.FromJson<UnityCtlSettings>(json);
                    if (settings != null)
                    {
                        return settings;
                    }
                }
                catch
                {
                    // Fall through to create new settings
                }
            }

            // Create new settings with defaults
            settings = new UnityCtlSettings
            {
                showDebugMessages = false // Default to false
            };
            settings.Save();

            return settings;
        }

        private void Save()
        {
            try
            {
                var json = JsonUtility.ToJson(this, true);
                File.WriteAllText(SettingsPath, json);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[UnityCtl] Failed to save settings: {ex.Message}");
            }
        }
    }
}
