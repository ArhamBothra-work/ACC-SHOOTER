using UnityEngine;

/// <summary>
/// Draws a simple crosshair in the middle of the screen, only for the local player.
/// Bullets should be fired from the camera along its forward direction, which
/// passes through this exact point. Put this on the Player root.
/// </summary>
[RequireComponent(typeof(PlayerController))]
public class Crosshair : MonoBehaviour
{
    [SerializeField] private Color color = Color.white;
    [SerializeField] private float length = 10f;
    [SerializeField] private float thickness = 2f;
    [SerializeField] private float gap = 5f;

    private PlayerController player;

    private void Awake()
    {
        player = GetComponent<PlayerController>();
    }

    private void OnGUI()
    {
        if (!player.IsSpawned || !player.IsOwner) return;

        float cx = Screen.width * 0.5f;
        float cy = Screen.height * 0.5f;
        var tex = Texture2D.whiteTexture;

        GUI.color = color;
        GUI.DrawTexture(new Rect(cx - thickness / 2f, cy - gap - length, thickness, length), tex); // up
        GUI.DrawTexture(new Rect(cx - thickness / 2f, cy + gap, thickness, length), tex);          // down
        GUI.DrawTexture(new Rect(cx - gap - length, cy - thickness / 2f, length, thickness), tex); // left
        GUI.DrawTexture(new Rect(cx + gap, cy - thickness / 2f, length, thickness), tex);          // right
        GUI.color = Color.white;
    }
}
