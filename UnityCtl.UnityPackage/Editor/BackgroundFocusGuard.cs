using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace UnityCtl
{
    /// <summary>
    /// Keeps the editor from stealing OS focus when play mode is entered while Unity
    /// is in the background (e.g. an agent driving it via unityctl while the user
    /// works in another app).
    ///
    /// Game views default to "Play Focused" (or "Play Maximized"), and both make Unity
    /// activate its main window on entering play mode — on Windows that pulls the
    /// editor to the foreground. This applies however play mode is entered
    /// (play.enter, script eval setting isPlaying, test runner, ...), so we hook
    /// playModeStateChanged instead of the play.enter command.
    ///
    /// When play mode starts and the editor is NOT the active application, every
    /// PlayModeView is temporarily switched to "Play Unfocused", then restored once
    /// play mode has been entered. If the user is in Unity, nothing changes.
    /// </summary>
    [InitializeOnLoad]
    internal static class BackgroundFocusGuard
    {
        // Survives the domain reload that (by default) happens between
        // ExitingEditMode and EnteredPlayMode.
        private const string OverridesKey = "UnityCtl.PlayFocusOverrides";

        private const string PlayUnfocused = "PlayUnfocused";

        private static readonly Type PlayModeViewType =
            typeof(EditorWindow).Assembly.GetType("UnityEditor.PlayModeView");

        private static readonly PropertyInfo BehaviorProperty =
            PlayModeViewType?.GetProperty("enterPlayModeBehavior",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        [Serializable]
        private class Override
        {
            public string window;
            public string original;
        }

        [Serializable]
        private class OverrideList
        {
            public List<Override> items = new List<Override>();
        }

        static BackgroundFocusGuard()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.ExitingEditMode:
                    if (!InternalEditorUtility.isApplicationActive)
                        OverrideToUnfocused();
                    break;

                case PlayModeStateChange.EnteredPlayMode:
                    // The game view decides whether to focus during this frame; restore
                    // after it has had its chance.
                    EditorApplication.delayCall += () => EditorApplication.delayCall += Restore;
                    break;

                case PlayModeStateChange.EnteredEditMode:
                    // Fallback in case entering play mode was aborted.
                    Restore();
                    break;
            }
        }

        private static IEnumerable<EditorWindow> PlayModeViews()
        {
            if (PlayModeViewType == null || BehaviorProperty == null)
                return Enumerable.Empty<EditorWindow>();
            return Resources.FindObjectsOfTypeAll(PlayModeViewType).OfType<EditorWindow>();
        }

        // UnityEngine.Object.GetHashCode() is the object's instance identity, which is
        // stable across domain reloads for windows that stay open — and avoids the
        // per-version InstanceID/EntityId API split. It only needs to match within a
        // single editor session.
        private static string Key(EditorWindow window) => window.GetHashCode().ToString();

        private static OverrideList Load()
        {
            var json = SessionState.GetString(OverridesKey, "");
            if (string.IsNullOrEmpty(json))
                return new OverrideList();
            try
            {
                return JsonUtility.FromJson<OverrideList>(json) ?? new OverrideList();
            }
            catch
            {
                return new OverrideList();
            }
        }

        private static void Save(OverrideList list)
        {
            if (list.items.Count == 0)
                SessionState.EraseString(OverridesKey);
            else
                SessionState.SetString(OverridesKey, JsonUtility.ToJson(list));
        }

        private static void OverrideToUnfocused()
        {
            try
            {
                var unfocused = Enum.Parse(BehaviorProperty.PropertyType, PlayUnfocused);

                // Merge with any earlier override that never got restored, so its
                // originals aren't lost.
                var saved = Load();
                var toChange = new List<EditorWindow>();
                foreach (var view in PlayModeViews())
                {
                    var current = BehaviorProperty.GetValue(view).ToString();
                    if (current == PlayUnfocused)
                        continue;
                    var key = Key(view);
                    if (!saved.items.Any(o => o.window == key))
                        saved.items.Add(new Override { window = key, original = current });
                    toChange.Add(view);
                }

                if (toChange.Count == 0)
                    return;

                // Record originals BEFORE changing anything, so a failure partway
                // through can still be restored.
                Save(saved);

                foreach (var view in toChange)
                    BehaviorProperty.SetValue(view, unfocused);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityCtl] Could not suppress play mode focus: {ex.Message}");
            }
        }

        private static void Restore()
        {
            var saved = Load();
            if (saved.items.Count == 0)
                return;

            var views = PlayModeViews().ToDictionary(Key);
            var remaining = new OverrideList();
            foreach (var entry in saved.items)
            {
                // Window closed since: nothing left to restore.
                if (!views.TryGetValue(entry.window, out var view))
                    continue;
                try
                {
                    BehaviorProperty.SetValue(view, Enum.Parse(BehaviorProperty.PropertyType, entry.original));
                }
                catch (Exception ex)
                {
                    // Keep the entry so the next restore attempt can retry it.
                    remaining.items.Add(entry);
                    Debug.LogWarning($"[UnityCtl] Could not restore play mode focus setting: {ex.Message}");
                }
            }

            Save(remaining);
        }
    }
}
