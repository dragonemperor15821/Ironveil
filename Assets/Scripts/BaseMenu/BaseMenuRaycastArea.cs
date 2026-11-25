using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Base Management Menu (Phase 1D): an invisible uGUI raycast target.
///
/// Receives pointer events like an Image but emits no geometry, so the
/// artwork underneath is shown exactly as supplied and the hit areas cost no
/// fill rate.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
[AddComponentMenu("")]
public sealed class BaseMenuRaycastArea : Graphic
{
    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
    }

    public override void SetMaterialDirty() { }
    public override void SetVerticesDirty() { }
}
