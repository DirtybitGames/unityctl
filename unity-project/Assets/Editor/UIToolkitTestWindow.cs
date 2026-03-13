using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public class UIToolkitTestWindow : EditorWindow
{
    [MenuItem("Window/Test/UI Toolkit Test Window")]
    public static void ShowWindow()
    {
        var window = GetWindow<UIToolkitTestWindow>();
        window.titleContent = new GUIContent("UIToolkit Test");
        window.minSize = new Vector2(300, 400);
    }

    public void CreateGUI()
    {
        var root = rootVisualElement;
        root.style.paddingTop = 8;
        root.style.paddingBottom = 8;
        root.style.paddingLeft = 8;
        root.style.paddingRight = 8;

        // Header
        var header = new Label("UI Toolkit Screenshot Test Window");
        header.style.fontSize = 16;
        header.style.unityFontStyleAndWeight = FontStyle.Bold;
        header.style.marginBottom = 8;
        root.Add(header);

        // Color banner
        var banner = new VisualElement();
        banner.style.flexDirection = FlexDirection.Row;
        banner.style.height = 30;
        banner.style.marginBottom = 8;
        var colors = new[] {
            new Color(0.9f, 0.2f, 0.2f), new Color(0.2f, 0.8f, 0.2f),
            new Color(0.2f, 0.4f, 0.9f), new Color(0.9f, 0.8f, 0.1f),
            new Color(0.8f, 0.2f, 0.8f), new Color(0.2f, 0.8f, 0.8f)
        };
        foreach (var c in colors)
        {
            var swatch = new VisualElement();
            swatch.style.flexGrow = 1;
            swatch.style.backgroundColor = c;
            swatch.style.borderTopLeftRadius = 4;
            swatch.style.borderTopRightRadius = 4;
            swatch.style.borderBottomLeftRadius = 4;
            swatch.style.borderBottomRightRadius = 4;
            swatch.style.marginRight = 2;
            banner.Add(swatch);
        }
        root.Add(banner);

        // Input fields
        var textField = new TextField("Text Field") { value = "Hello UI Toolkit" };
        root.Add(textField);

        var slider = new Slider("Slider", 0, 1) { value = 0.7f };
        slider.showInputField = true;
        root.Add(slider);

        var intSlider = new SliderInt("Int Slider", 0, 100) { value = 42 };
        intSlider.showInputField = true;
        root.Add(intSlider);

        var toggle = new Toggle("Toggle") { value = true };
        root.Add(toggle);

        var dropdown = new DropdownField("Dropdown", new() { "Alpha", "Beta", "Gamma", "Delta" }, 1);
        root.Add(dropdown);

        var vector3 = new Vector3Field("Position") { value = new Vector3(1.5f, 2.0f, 3.14f) };
        root.Add(vector3);

        // Separator
        var sep = new VisualElement();
        sep.style.height = 1;
        sep.style.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
        sep.style.marginTop = 8;
        sep.style.marginBottom = 8;
        root.Add(sep);

        // Foldout with list items
        var foldout = new Foldout { text = "List Items", value = true };
        for (int i = 0; i < 5; i++)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.paddingTop = 2;
            row.style.paddingBottom = 2;

            var icon = new VisualElement();
            icon.style.width = 12;
            icon.style.height = 12;
            icon.style.borderTopLeftRadius = 6;
            icon.style.borderTopRightRadius = 6;
            icon.style.borderBottomLeftRadius = 6;
            icon.style.borderBottomRightRadius = 6;
            icon.style.backgroundColor = colors[i % colors.Length];
            icon.style.marginRight = 6;
            row.Add(icon);

            var label = new Label($"Item {i + 1} — Description text for this row");
            label.style.flexGrow = 1;
            row.Add(label);

            var badge = new Label($"{(i + 1) * 10}");
            badge.style.backgroundColor = new Color(0.3f, 0.3f, 0.3f);
            badge.style.color = Color.white;
            badge.style.borderTopLeftRadius = 8;
            badge.style.borderTopRightRadius = 8;
            badge.style.borderBottomLeftRadius = 8;
            badge.style.borderBottomRightRadius = 8;
            badge.style.paddingLeft = 6;
            badge.style.paddingRight = 6;
            badge.style.fontSize = 11;
            row.Add(badge);

            foldout.Add(row);
        }
        root.Add(foldout);

        // Progress bars
        var progressLabel = new Label("Progress Bars");
        progressLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        progressLabel.style.marginTop = 8;
        root.Add(progressLabel);

        root.Add(CreateProgressBar(0.75f, "Loading 75%", new Color(0.2f, 0.6f, 1f)));
        root.Add(CreateProgressBar(0.33f, "Processing 33%", new Color(0.2f, 0.8f, 0.3f)));
        root.Add(CreateProgressBar(0.9f, "Almost done 90%", new Color(0.9f, 0.6f, 0.1f)));

        // Button
        var button = new Button(() => Debug.Log("UI Toolkit button clicked")) { text = "Click Me" };
        button.style.height = 30;
        button.style.marginTop = 8;
        root.Add(button);

        // Footer
        var footer = new HelpBox("This window tests UI Toolkit rendering for screenshot capture.", HelpBoxMessageType.Info);
        footer.style.marginTop = 8;
        root.Add(footer);
    }

    private static VisualElement CreateProgressBar(float value, string label, Color color)
    {
        var container = new VisualElement();
        container.style.height = 22;
        container.style.marginTop = 4;
        container.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f);
        container.style.borderTopLeftRadius = 3;
        container.style.borderTopRightRadius = 3;
        container.style.borderBottomLeftRadius = 3;
        container.style.borderBottomRightRadius = 3;
        container.style.overflow = Overflow.Hidden;

        var fill = new VisualElement();
        fill.style.position = Position.Absolute;
        fill.style.left = 0;
        fill.style.top = 0;
        fill.style.bottom = 0;
        fill.style.width = Length.Percent(value * 100);
        fill.style.backgroundColor = color;
        container.Add(fill);

        var text = new Label(label);
        text.style.position = Position.Absolute;
        text.style.left = 0;
        text.style.right = 0;
        text.style.top = 0;
        text.style.bottom = 0;
        text.style.unityTextAlign = TextAnchor.MiddleCenter;
        text.style.color = Color.white;
        text.style.fontSize = 11;
        container.Add(text);

        return container;
    }
}
