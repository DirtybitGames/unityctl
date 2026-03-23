using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// A custom interactive UI component that does NOT inherit from Selectable.
/// Used to test that the broadened snapshot detection picks up IPointerClickHandler
/// and the "text" property convention.
/// </summary>
public class CustomClickHandler : MonoBehaviour, IPointerClickHandler
{
    public string text = "Custom Action";
    public bool interactable = true;

    public void OnPointerClick(PointerEventData eventData)
    {
        if (!interactable) return;
        Debug.Log($"CustomClickHandler clicked: {text}");
    }
}
