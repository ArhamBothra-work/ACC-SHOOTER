using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Slants any Image or Text (like an italic "/" lean). Gives the UI an angled, aggressive shooter look.
/// Added automatically by UIKit; you never add this by hand.
/// </summary>
[AddComponentMenu("UI/Effects/Skew")]
public class UISkew : BaseMeshEffect
{
    public float skew = 0.2f;

    public override void ModifyMesh(VertexHelper vh)
    {
        if (!IsActive() || vh.currentVertCount == 0) return;

        float cy = ((RectTransform)transform).rect.center.y;
        var v = new UIVertex();
        for (int i = 0; i < vh.currentVertCount; i++)
        {
            vh.PopulateUIVertex(ref v, i);
            v.position.x += (v.position.y - cy) * skew;
            vh.SetUIVertex(v, i);
        }
    }
}
