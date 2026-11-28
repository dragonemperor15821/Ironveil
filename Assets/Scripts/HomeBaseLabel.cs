using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Stage 8B: the visible "HOME BASE" marking: a world-space UI text above the base, turned towards the camera.
/// UI text uses a CanvasRenderer (not a Renderer), so the base's hover/selection bounds, which are built from its
/// mesh renderers, are unaffected. Created at runtime; no prefab or model is changed.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("")]
public sealed class HomeBaseLabel : MonoBehaviour
{
    public const string LabelObjectName = "HOME BASE Label";
    private const float CanvasWidth = 600f;
    private const float CanvasHeight = 140f;

    [SerializeField] private Text text;
    [SerializeField] private Canvas canvas;

    public Text Text => text;
    public Canvas Canvas => canvas;

    public static HomeBaseLabel Ensure(BaseController controller)
    {
        HomeBaseLabel label = controller.GetComponentInChildren<HomeBaseLabel>(true);
        if (label != null) return label;

        // Size and height from the base's own geometry (measured before the label exists).
        Bounds bounds = new Bounds(controller.transform.position, Vector3.zero);
        bool any = false;
        foreach (Renderer r in controller.GetComponentsInChildren<Renderer>(true))
        {
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
            if (any) bounds.Encapsulate(r.bounds); else { bounds = r.bounds; any = true; }
        }
        float width = Mathf.Max(1f, Mathf.Max(bounds.size.x, bounds.size.z));

        var go = new GameObject(LabelObjectName, typeof(RectTransform));
        go.transform.SetParent(controller.transform, true);
        go.transform.position = new Vector3(bounds.center.x, bounds.max.y + Mathf.Max(1f, bounds.size.y * 0.35f), bounds.center.z);
        go.transform.rotation = Quaternion.identity;
        float scale = width * 0.8f / CanvasWidth;
        go.transform.localScale = Vector3.one;
        go.transform.localScale = new Vector3(scale / Mathf.Max(1e-6f, go.transform.lossyScale.x),
                                              scale / Mathf.Max(1e-6f, go.transform.lossyScale.y),
                                              scale / Mathf.Max(1e-6f, go.transform.lossyScale.z));

        label = go.AddComponent<HomeBaseLabel>();
        label.canvas = go.AddComponent<Canvas>();
        label.canvas.renderMode = RenderMode.WorldSpace;
        ((RectTransform)go.transform).sizeDelta = new Vector2(CanvasWidth, CanvasHeight);

        var textGo = new GameObject("Text", typeof(RectTransform));
        textGo.transform.SetParent(go.transform, false);
        RectTransform rt = (RectTransform)textGo.transform;
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        label.text = textGo.AddComponent<Text>();
        label.text.text = PlayerHomeBase.LabelText;
        label.text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.text.fontSize = 96;
        label.text.fontStyle = FontStyle.Bold;
        label.text.alignment = TextAnchor.MiddleCenter;
        label.text.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.text.verticalOverflow = VerticalWrapMode.Overflow;
        label.text.color = new Color(1f, 0.82f, 0.25f, 1f);
        label.text.raycastTarget = false;
        Outline outline = textGo.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
        outline.effectDistance = new Vector2(3f, -3f);
        return label;
    }

    private void LateUpdate()
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        // Face the camera (readable from any RTS angle).
        transform.rotation = Quaternion.LookRotation(transform.position - cam.transform.position, cam.transform.up);
    }
}
