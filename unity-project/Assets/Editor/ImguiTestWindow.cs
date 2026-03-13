using UnityEditor;
using UnityEngine;

public class ImguiTestWindow : EditorWindow
{
    private string _inputText = "Hello IMGUI";
    private float _slider = 0.5f;
    private bool _toggle = true;
    private int _selectedTab;
    private Vector2 _scrollPos;
    private Color _color = Color.cyan;
    private AnimationCurve _curve = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [MenuItem("Window/Test/IMGUI Test Window")]
    public static void ShowWindow()
    {
        var window = GetWindow<ImguiTestWindow>();
        window.titleContent = new GUIContent("IMGUI Test");
        window.minSize = new Vector2(300, 400);
    }

    private void OnGUI()
    {
        // Header
        GUILayout.Label("IMGUI Screenshot Test Window", EditorStyles.boldLabel);
        EditorGUILayout.Space(8);

        // Tabs
        _selectedTab = GUILayout.Toolbar(_selectedTab, new[] { "Controls", "Layout", "Graphics" });
        EditorGUILayout.Space(4);

        _scrollPos = EditorGUILayout.BeginScrollView(_scrollPos);

        switch (_selectedTab)
        {
            case 0: DrawControls(); break;
            case 1: DrawLayout(); break;
            case 2: DrawGraphics(); break;
        }

        EditorGUILayout.EndScrollView();

        // Footer
        EditorGUILayout.Space(4);
        EditorGUILayout.HelpBox("This window tests IMGUI rendering for screenshot capture.", MessageType.Info);
    }

    private void DrawControls()
    {
        _inputText = EditorGUILayout.TextField("Text Field", _inputText);
        _slider = EditorGUILayout.Slider("Slider", _slider, 0f, 1f);
        _toggle = EditorGUILayout.Toggle("Toggle", _toggle);
        _color = EditorGUILayout.ColorField("Color", _color);
        _curve = EditorGUILayout.CurveField("Curve", _curve);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Dropdown:");
        EditorGUILayout.Popup(1, new[] { "Option A", "Option B", "Option C" });

        EditorGUILayout.Space(8);
        if (GUILayout.Button("Click Me", GUILayout.Height(30)))
            Debug.Log("Button clicked");

        EditorGUILayout.Space(4);
        EditorGUILayout.MinMaxSlider("Range", ref _slider, ref _slider, 0f, 1f);

        EditorGUILayout.Space(8);
        EditorGUILayout.IntSlider("Int Slider", 42, 0, 100);
        EditorGUILayout.Vector3Field("Position", Vector3.one * 3.14f);
    }

    private void DrawLayout()
    {
        EditorGUILayout.LabelField("Foldout Groups", EditorStyles.boldLabel);

        for (int i = 0; i < 5; i++)
        {
            EditorGUILayout.BeginHorizontal("box");
            EditorGUILayout.LabelField($"Item {i + 1}", GUILayout.Width(80));
            EditorGUILayout.LabelField($"Value: {Random.Range(0, 100)}", EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("X", GUILayout.Width(20))) { }
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Progress Bars", EditorStyles.boldLabel);
        EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(GUILayout.Height(20)), 0.75f, "Loading 75%");
        EditorGUI.ProgressBar(EditorGUILayout.GetControlRect(GUILayout.Height(20)), 0.33f, "Processing 33%");
    }

    private void DrawGraphics()
    {
        EditorGUILayout.LabelField("Color Swatches", EditorStyles.boldLabel);

        var rect = EditorGUILayout.GetControlRect(GUILayout.Height(40));
        var colors = new[] { Color.red, Color.green, Color.blue, Color.yellow, Color.magenta, Color.cyan };
        float w = rect.width / colors.Length;
        for (int i = 0; i < colors.Length; i++)
        {
            EditorGUI.DrawRect(new Rect(rect.x + i * w, rect.y, w - 2, rect.height), colors[i]);
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Styled Labels", EditorStyles.boldLabel);

        var style = new GUIStyle(EditorStyles.label) { richText = true };
        EditorGUILayout.LabelField("<color=red>Red</color> <color=green>Green</color> <color=blue>Blue</color>", style);
        EditorGUILayout.LabelField("<b>Bold</b> <i>Italic</i> <size=18>Large</size>", style);

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Texture Preview", EditorStyles.boldLabel);
        var previewRect = EditorGUILayout.GetControlRect(GUILayout.Height(64));
        EditorGUI.DrawTextureTransparent(previewRect, EditorGUIUtility.whiteTexture);

        // Draw checkerboard pattern
        for (int x = 0; x < 8; x++)
        {
            for (int y = 0; y < 4; y++)
            {
                if ((x + y) % 2 == 0)
                {
                    var cellRect = new Rect(
                        previewRect.x + x * (previewRect.width / 8),
                        previewRect.y + y * (previewRect.height / 4),
                        previewRect.width / 8, previewRect.height / 4);
                    EditorGUI.DrawRect(cellRect, new Color(0.3f, 0.3f, 0.3f));
                }
            }
        }
    }
}
