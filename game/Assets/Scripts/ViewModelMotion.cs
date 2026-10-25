using UnityEngine;

/// <summary>
/// Puts life into the first-person gun: bobs while you walk/run and lags behind
/// when you turn. Put this on the GunViewModel (assault1). Owner-only, because the
/// viewmodel is disabled on remote players.
/// </summary>
public class ViewModelMotion : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform player;      // the Player root
    [SerializeField] private Transform cameraRoot;  // used to read look pitch

    [Header("Walk bob")]
    [SerializeField] private float bobRate = 1.8f;      // steps per unit of speed
    [SerializeField] private float bobAmountX = 0.012f;
    [SerializeField] private float bobAmountY = 0.010f;
    [SerializeField] private float maxSpeed = 8f;       // your run speed

    [Header("Look sway")]
    [SerializeField] private float swayAmount = 0.02f;  // degrees per (deg/sec) of turning
    [SerializeField] private float maxSway = 5f;        // clamp in degrees
    [SerializeField] private float swaySmoothing = 10f;

    private Vector3 startPos;
    private Quaternion startRot;
    private Vector3 lastPlayerPos;
    private float lastYaw, lastPitch;
    private float bobTimer;
    private float moveBlend;
    private Vector2 swayEuler;

    private void OnEnable()
    {
        startPos = transform.localPosition;
        startRot = transform.localRotation;
        lastPlayerPos = player.position;
        lastYaw = player.eulerAngles.y;
        lastPitch = cameraRoot.localEulerAngles.x;
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        // --- movement speed from actual movement ---
        Vector3 delta = player.position - lastPlayerPos;
        delta.y = 0f;
        float speed = delta.magnitude / dt;
        lastPlayerPos = player.position;

        moveBlend = Mathf.Lerp(moveBlend, Mathf.Clamp01(speed / maxSpeed), 8f * dt);
        bobTimer += speed * bobRate * dt;

        Vector3 bob = new Vector3(
            Mathf.Cos(bobTimer * 0.5f) * bobAmountX,
            Mathf.Sin(bobTimer) * bobAmountY,
            0f) * moveBlend;

        // --- look sway (gun lags behind your turning) ---
        float yaw = player.eulerAngles.y;
        float pitch = cameraRoot.localEulerAngles.x;
        float yawVel = Mathf.DeltaAngle(lastYaw, yaw) / dt;
        float pitchVel = Mathf.DeltaAngle(lastPitch, pitch) / dt;
        lastYaw = yaw;
        lastPitch = pitch;

        Vector2 target = new Vector2(
            Mathf.Clamp(-pitchVel * swayAmount, -maxSway, maxSway),
            Mathf.Clamp(-yawVel * swayAmount, -maxSway, maxSway));
        swayEuler = Vector2.Lerp(swayEuler, target, swaySmoothing * dt);

        transform.localPosition = startPos + bob;
        transform.localRotation = startRot * Quaternion.Euler(swayEuler.x, swayEuler.y, 0f);
    }
}
