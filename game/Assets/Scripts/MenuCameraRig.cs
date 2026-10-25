using UnityEngine;

/// <summary>
/// Put on MenuCamera. Frames the two hero characters like a game cover:
/// low camera, narrow lens, slow sway, heroes pushed to the RIGHT of the screen
/// (the menu text sits on the left), plus blue / red rim lights and a warm key light.
/// </summary>
[RequireComponent(typeof(Camera))]
public class MenuCameraRig : MonoBehaviour
{
    [Header("Framing")]
    public Transform target;            // StageCenter (on the ground between the heroes)
    public float lookHeight = 1.15f;    // aim point above the ground (chest height)
    public float distance = 4.2f;       // camera distance in front of StageCenter (its blue Z arrow side)
    public float height = 0.05f;        // camera height relative to the aim point (negative = looking up at them)
    public float screenOffset = 1.4f;   // bigger = heroes further right on screen
    public float fieldOfView = 30f;

    [Header("Motion")]
    public float swayDegrees = 4f;
    public float swaySpeed = 0.25f;

    [Header("Lights")]
    public bool autoLights = true;
    public Color blueRim = new Color(0.25f, 0.55f, 1f);
    public Color redRim = new Color(1f, 0.25f, 0.2f);
    public Color keyColor = new Color(1f, 0.9f, 0.75f);
    public float rimIntensity = 8f;
    public float keyIntensity = 3f;
    public float lightRange = 9f;

    Camera cam;

    void Awake()
    {
        cam = GetComponent<Camera>();
        cam.fieldOfView = fieldOfView;
    }

    void Start()
    {
        if (autoLights && target != null) BuildLights();
    }

    void LateUpdate()
    {
        if (target == null || !cam.enabled) return;

        Vector3 center = target.position + Vector3.up * lookHeight;
        float a = Mathf.Sin(Time.time * swaySpeed) * swayDegrees;
        Vector3 dir = Quaternion.AngleAxis(a, Vector3.up) * target.forward;

        transform.position = center + dir * distance + Vector3.up * height;
        transform.LookAt(center);
        transform.position -= transform.right * screenOffset;
    }

    void BuildLights()
    {
        var root = new GameObject("StageLights").transform;
        root.SetParent(target, false);
        MakeLight(root, "RimBlue", blueRim, new Vector3(-2.3f, 2.2f, -1.3f), rimIntensity);
        MakeLight(root, "RimRed", redRim, new Vector3(2.3f, 2.2f, -1.3f), rimIntensity);
        MakeLight(root, "Key", keyColor, new Vector3(0.6f, 2.4f, 2.6f), keyIntensity);
    }

    void MakeLight(Transform parent, string n, Color c, Vector3 localPos, float intensity)
    {
        var go = new GameObject(n);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        var l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = c;
        l.intensity = intensity;
        l.range = lightRange;
        l.shadows = LightShadows.None;
    }
}
