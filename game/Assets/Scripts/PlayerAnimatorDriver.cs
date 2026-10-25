using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Drives the Mixamo Animator from the player's ACTUAL movement, so it works
/// identically for the owner and for remote copies with no animator syncing needed.
/// Put on the Player root. Assign the Animator on Ch15_nonPBR.
///
/// Animator parameters required:
///   Speed (float), MoveX (float), MoveZ (float), Grounded (bool), Jump (trigger)
/// On spawn this script CHECKS the controller and prints a clear red error if something is wrong.
/// </summary>
public class PlayerAnimatorDriver : NetworkBehaviour
{
    [SerializeField] private Animator animator;
    [SerializeField] private float damp = 0.1f;
    [SerializeField] private float groundCheckDistance = 1.1f; // root is at capsule centre; feet are 0.9 below. Must be > 0.9
    [SerializeField] private LayerMask groundMask = ~0;

    [Header("Debug (display only - watch these while walking)")]
    [SerializeField] private string debugController;
    [SerializeField] private float debugSpeed;
    [SerializeField] private bool debugGrounded;

    private static readonly string[] Required = { "Speed", "MoveX", "MoveZ", "Grounded", "Jump" };
    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int MoveXHash = Animator.StringToHash("MoveX");
    private static readonly int MoveZHash = Animator.StringToHash("MoveZ");
    private static readonly int GroundedHash = Animator.StringToHash("Grounded");
    private static readonly int JumpHash = Animator.StringToHash("Jump");

    private Vector3 _lastPos;
    private bool _wasGrounded = true;
    private bool _ready;

    public override void OnNetworkSpawn()
    {
        _lastPos = transform.position;
        _ready = ValidateAnimator();
    }

    private bool ValidateAnimator()
    {
        if (animator == null)
        {
            Debug.LogError("[Anim] The Animator slot on PlayerAnimatorDriver is EMPTY.", this);
            return false;
        }

        animator.applyRootMotion = false;                          // the network moves the player, not the animation
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;  // keep animating even when off-screen

        if (animator.runtimeAnimatorController == null)
        {
            Debug.LogError($"[Anim] Animator on '{animator.name}' has NO controller assigned.", this);
            return false;
        }

        debugController = animator.runtimeAnimatorController.name;

        bool ok = true;
        foreach (string req in Required)
        {
            bool found = false;
            foreach (var p in animator.parameters) if (p.name == req) { found = true; break; }
            if (!found)
            {
                Debug.LogError($"[Anim] Controller '{debugController}' is missing parameter '{req}'. " +
                               "Build a new one with Tools > TDM > Build Rifle Animator Controller and assign it.", this);
                ok = false;
            }
        }
        return ok;
    }

    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (!_ready || dt <= 0f) return;

        Vector3 worldVel = (transform.position - _lastPos) / dt;
        _lastPos = transform.position;

        // A huge jump in one frame = respawn teleport, not running. Ignore it.
        if (worldVel.magnitude > 40f) worldVel = Vector3.zero;

        Vector3 localVel = transform.InverseTransformDirection(worldVel);
        Vector3 flat = new Vector3(localVel.x, 0f, localVel.z);

        animator.SetFloat(SpeedHash, flat.magnitude, damp, dt);
        animator.SetFloat(MoveXHash, flat.x, damp, dt);
        animator.SetFloat(MoveZHash, flat.z, damp, dt);

        bool grounded = Physics.Raycast(transform.position + Vector3.up * 0.1f, Vector3.down,
            groundCheckDistance, groundMask, QueryTriggerInteraction.Ignore);

        if (_wasGrounded && !grounded && worldVel.y > 0.5f)
            animator.SetTrigger(JumpHash);

        animator.SetBool(GroundedHash, grounded);
        _wasGrounded = grounded;

        debugSpeed = flat.magnitude;
        debugGrounded = grounded;
    }
}