using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// A custom interactive UI component that does NOT inherit from Selectable.
/// Used to test that snapshot detection picks up IPointerClickHandler.
/// </summary>
public class CustomClickHandler : MonoBehaviour, IPointerClickHandler
{
    public void OnPointerClick(PointerEventData eventData)
    {
        Debug.Log("CustomClickHandler clicked");
    }
}
