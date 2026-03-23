using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Creates a test scene designed to exercise all snapshot command features:
/// - 3D objects with colliders (for --screen bounds and query raycasting)
/// - UI Canvas with buttons, text, overlapping panels (for hittability/occlusion testing)
/// - Mixed hierarchy depths
/// - Tagged/layered objects
/// - A custom interactive component (not inheriting from Selectable)
/// </summary>
public static class CreateSnapshotTestScene
{
    [MenuItem("Tools/Create Snapshot Test Scene")]
    public static void Create()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // --- Camera ---
        var cameraGo = new GameObject("Main Camera");
        cameraGo.tag = "MainCamera";
        var cam = cameraGo.AddComponent<Camera>();
        cam.orthographic = false;
        cam.fieldOfView = 60;
        cameraGo.transform.position = new Vector3(0, 5, -10);
        cameraGo.transform.rotation = Quaternion.Euler(20, 0, 0);
        cameraGo.AddComponent<AudioListener>();

        // --- Directional Light ---
        var lightGo = new GameObject("Directional Light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);

        // --- 3D Objects ---

        // Ground plane (large, flat — for query raycasting)
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.position = Vector3.zero;
        ground.transform.localScale = new Vector3(5, 1, 5);
        ground.layer = LayerMask.NameToLayer("Default");

        // Player cube — tagged, with TestComponent
        var player = GameObject.CreatePrimitive(PrimitiveType.Cube);
        player.name = "Player";
        player.tag = "Player";
        player.transform.position = new Vector3(0, 1.5f, 0);
        var tc = player.AddComponent<TestComponent>();
        tc.speed = 7.5f;
        tc.health = 100;
        tc.displayName = "Hero";

        // Child object under Player (tests hierarchy depth)
        var weapon = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        weapon.name = "Weapon";
        weapon.transform.SetParent(player.transform);
        weapon.transform.localPosition = new Vector3(0.8f, 0, 0);
        weapon.transform.localScale = new Vector3(0.2f, 0.5f, 0.2f);

        // Enemy sphere — on a different layer
        var enemy = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        enemy.name = "Enemy";
        enemy.transform.position = new Vector3(4, 1, 3);

        // Partially off-screen object (far right)
        var farCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        farCube.name = "FarCube";
        farCube.transform.position = new Vector3(20, 1, 0);

        // Object behind another (for 3D occlusion testing)
        var hiddenSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        hiddenSphere.name = "HiddenBehindPlayer";
        hiddenSphere.transform.position = new Vector3(0, 1.5f, 2); // behind Player from camera's perspective

        // --- UI Canvas (Screen Space Overlay) ---
        var canvasGo = new GameObject("Canvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasGo.AddComponent<CanvasScaler>();
        canvasGo.AddComponent<GraphicRaycaster>();

        // EventSystem (needed for UI raycasting)
        var eventSystemGo = new GameObject("EventSystem");
        eventSystemGo.AddComponent<EventSystem>();
        eventSystemGo.AddComponent<StandaloneInputModule>();

        // --- UI Panel (background) ---
        var panel = CreateUIElement<Image>("Panel", canvasGo.transform);
        panel.color = new Color(0, 0, 0, 0.5f);
        var panelRt = panel.GetComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0, 0.7f);
        panelRt.anchorMax = new Vector2(0.4f, 1f);
        panelRt.offsetMin = Vector2.zero;
        panelRt.offsetMax = Vector2.zero;

        // --- Title text ---
        var titleGo = new GameObject("Title");
        titleGo.transform.SetParent(panel.transform, false);
        var titleRt = titleGo.AddComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0, 0.6f);
        titleRt.anchorMax = new Vector2(1, 1);
        titleRt.offsetMin = new Vector2(10, 0);
        titleRt.offsetMax = new Vector2(-10, -5);
        var titleText = titleGo.AddComponent<Text>();
        titleText.text = "Snapshot Test";
        titleText.fontSize = 24;
        titleText.alignment = TextAnchor.MiddleCenter;
        titleText.color = Color.white;
        titleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // --- Button (interactable) ---
        var buttonGo = new GameObject("PlayButton");
        buttonGo.transform.SetParent(panel.transform, false);
        var buttonRt = buttonGo.AddComponent<RectTransform>();
        buttonRt.anchorMin = new Vector2(0.1f, 0.1f);
        buttonRt.anchorMax = new Vector2(0.45f, 0.5f);
        buttonRt.offsetMin = Vector2.zero;
        buttonRt.offsetMax = Vector2.zero;
        var buttonImg = buttonGo.AddComponent<Image>();
        buttonImg.color = new Color(0.2f, 0.6f, 1f, 1f);
        var button = buttonGo.AddComponent<Button>();

        // Button label
        var buttonLabel = new GameObject("Label");
        buttonLabel.transform.SetParent(buttonGo.transform, false);
        var labelRt = buttonLabel.AddComponent<RectTransform>();
        labelRt.anchorMin = Vector2.zero;
        labelRt.anchorMax = Vector2.one;
        labelRt.offsetMin = Vector2.zero;
        labelRt.offsetMax = Vector2.zero;
        var labelText = buttonLabel.AddComponent<Text>();
        labelText.text = "Play";
        labelText.fontSize = 18;
        labelText.alignment = TextAnchor.MiddleCenter;
        labelText.color = Color.white;
        labelText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // --- Disabled button ---
        var disabledGo = new GameObject("DisabledButton");
        disabledGo.transform.SetParent(panel.transform, false);
        var disabledRt = disabledGo.AddComponent<RectTransform>();
        disabledRt.anchorMin = new Vector2(0.55f, 0.1f);
        disabledRt.anchorMax = new Vector2(0.9f, 0.5f);
        disabledRt.offsetMin = Vector2.zero;
        disabledRt.offsetMax = Vector2.zero;
        var disabledImg = disabledGo.AddComponent<Image>();
        disabledImg.color = new Color(0.5f, 0.5f, 0.5f, 1f);
        var disabledBtn = disabledGo.AddComponent<Button>();
        disabledBtn.interactable = false;

        var disabledLabel = new GameObject("Label");
        disabledLabel.transform.SetParent(disabledGo.transform, false);
        var dlRt = disabledLabel.AddComponent<RectTransform>();
        dlRt.anchorMin = Vector2.zero;
        dlRt.anchorMax = Vector2.one;
        dlRt.offsetMin = Vector2.zero;
        dlRt.offsetMax = Vector2.zero;
        var dlText = disabledLabel.AddComponent<Text>();
        dlText.text = "Locked";
        dlText.fontSize = 18;
        dlText.alignment = TextAnchor.MiddleCenter;
        dlText.color = Color.white;
        dlText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // --- Overlapping UI: a modal overlay that blocks the button ---
        var overlay = CreateUIElement<Image>("ModalOverlay", canvasGo.transform);
        overlay.color = new Color(0, 0, 0, 0.3f);
        overlay.raycastTarget = true;
        var overlayRt = overlay.GetComponent<RectTransform>();
        // Covers the entire panel area — should block clicks on PlayButton
        overlayRt.anchorMin = new Vector2(0, 0.7f);
        overlayRt.anchorMax = new Vector2(0.4f, 1f);
        overlayRt.offsetMin = Vector2.zero;
        overlayRt.offsetMax = Vector2.zero;

        // Text on the overlay
        var overlayText = new GameObject("OverlayText");
        overlayText.transform.SetParent(overlay.transform, false);
        var otRt = overlayText.AddComponent<RectTransform>();
        otRt.anchorMin = new Vector2(0, 0.3f);
        otRt.anchorMax = new Vector2(1, 0.7f);
        otRt.offsetMin = Vector2.zero;
        otRt.offsetMax = Vector2.zero;
        var otText = overlayText.AddComponent<Text>();
        otText.text = "Loading...";
        otText.fontSize = 20;
        otText.alignment = TextAnchor.MiddleCenter;
        otText.color = Color.white;
        otText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // --- Separate unblocked UI in bottom-right corner ---
        var healthBar = CreateUIElement<Image>("HealthBar", canvasGo.transform);
        healthBar.color = new Color(0.8f, 0.1f, 0.1f, 1f);
        var hbRt = healthBar.GetComponent<RectTransform>();
        hbRt.anchorMin = new Vector2(0.7f, 0);
        hbRt.anchorMax = new Vector2(1f, 0.05f);
        hbRt.offsetMin = Vector2.zero;
        hbRt.offsetMax = Vector2.zero;

        // --- Custom interactive component (no Selectable inheritance) ---
        var customBtnGo = new GameObject("CustomButton");
        customBtnGo.transform.SetParent(canvasGo.transform, false);
        var customRt = customBtnGo.AddComponent<RectTransform>();
        customRt.anchorMin = new Vector2(0.7f, 0.9f);
        customRt.anchorMax = new Vector2(0.95f, 0.98f);
        customRt.offsetMin = Vector2.zero;
        customRt.offsetMax = Vector2.zero;
        var customImg = customBtnGo.AddComponent<Image>();
        customImg.color = new Color(1f, 0.8f, 0f, 1f);
        customBtnGo.AddComponent<CustomClickHandler>();

        // Save the scene
        EditorSceneManager.SaveScene(scene, "Assets/Scenes/SnapshotTestScene.unity");
        Debug.Log("Created SnapshotTestScene at Assets/Scenes/SnapshotTestScene.unity");
    }

    private static T CreateUIElement<T>(string name, Transform parent) where T : Component
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        return go.AddComponent<T>();
    }
}
