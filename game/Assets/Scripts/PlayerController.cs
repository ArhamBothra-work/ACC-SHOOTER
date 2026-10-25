using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// First-person controller (NEW Input System) for a Netcode for GameObjects player.
/// Owner: camera + viewmodel gun, body hidden. Remote players: full humanoid body.
/// Animation is handled by PlayerAnimatorDriver (not here).
/// Tab = free / re-lock the mouse cursor.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerController : NetworkBehaviour
{
    [Header("Input")]
    [SerializeField] private InputActionAsset inputActions;   // drag InputSystem_Actions here

    [Header("References")]
    [SerializeField] private Camera playerCamera;
    [SerializeField] private AudioListener audioListener;
    [SerializeField] private Transform cameraRoot;
    [SerializeField] private GameObject viewModel;
    [SerializeField] private GameObject bodyRoot;

    [Header("Movement")]
    [SerializeField] private float walkSpeed = 5f;
    [SerializeField] private float runSpeed = 8f;
    [SerializeField] private float jumpHeight = 1.2f;
    [SerializeField] private float gravity = -20f;

    [Header("Look")]
    [SerializeField] private float mouseSensitivity = 0.1f;
    [SerializeField] private float maxPitch = 85f;

    private CharacterController controller;
    private Health health;
    private InputAction moveAction, lookAction, jumpAction, sprintAction;
    private InputAction previousWeaponAction, nextWeaponAction;
    private InputActionAsset inputInstance;
    private float pitch;
    private float verticalVelocity;
    private CursorLockMode _lastLoggedCursorLockState;
    private bool _lastLoggedCursorVisibility;
    private bool _lastLoggedFocus;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        health = GetComponent<Health>();
    }

    public override void OnNetworkSpawn()
    {
        // The CharacterController only moves YOUR player. Other players are moved by the network.
        // (Bullets hit the separate CapsuleCollider hitbox, so remote players are still shootable.)
        controller.enabled = IsOwner;

        if (IsOwner)
        {
            _lastLoggedFocus = Application.isFocused;
            playerCamera.enabled = true;
            audioListener.enabled = true;
            viewModel.SetActive(true);

            foreach (var r in bodyRoot.GetComponentsInChildren<Renderer>())
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;

            // Clone so several players in one process never share input state.
            inputInstance = Instantiate(inputActions);
            var map = inputInstance.FindActionMap("Player", true);
            moveAction = map.FindAction("Move", true);
            lookAction = map.FindAction("Look", true);
            jumpAction = map.FindAction("Jump", true);
            sprintAction = map.FindAction("Sprint", true);
            previousWeaponAction = map.FindAction("Previous", false);
            nextWeaponAction = map.FindAction("Next", false);
            map.Enable();

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _lastLoggedCursorLockState = Cursor.lockState;
            _lastLoggedCursorVisibility = Cursor.visible;
            Debug.Log($"[InputDiag][Player] Local player spawned; ownerClientId={OwnerClientId}, componentEnabled={enabled}, inputMap={map.name}, inputMapEnabled={map.enabled}, moveEnabled={moveAction.enabled}, focus={Application.isFocused}, cursor={Cursor.lockState}, cursorVisible={Cursor.visible}, networkConnected={NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient}.");
        }
        else
        {
            playerCamera.enabled = false;
            audioListener.enabled = false;
            viewModel.SetActive(false);
            Debug.Log($"[InputDiag][Player] Remote player spawned; ownerClientId={OwnerClientId}, localClientId={(NetworkManager.Singleton != null ? NetworkManager.Singleton.LocalClientId.ToString() : "unavailable")}.");
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner)
            Debug.Log($"[InputDiag][Player] Local player despawned; ownerClientId={OwnerClientId}, componentEnabled={enabled}, focus={Application.isFocused}, cursor={Cursor.lockState}, networkListening={NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening}.");

        if (inputInstance != null)
        {
            inputInstance.Disable();
            Destroy(inputInstance);
        }
    }

    public override void OnGainedOwnership()
    {
        Debug.Log($"[InputDiag][Player] Local player ownership gained; ownerClientId={OwnerClientId}, componentEnabled={enabled}, inputInstanceActive={inputInstance != null && inputInstance.enabled}, focus={Application.isFocused}, cursor={Cursor.lockState}.");
    }

    public override void OnLostOwnership()
    {
        Debug.LogWarning($"[InputDiag][Player] Local player ownership lost; ownerClientId={OwnerClientId}, componentEnabled={enabled}, focus={Application.isFocused}, cursor={Cursor.lockState}.");
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (!IsSpawned || !IsOwner || _lastLoggedFocus == hasFocus) return;

        _lastLoggedFocus = hasFocus;
        Debug.Log($"[InputDiag][Player] Application input focus {(hasFocus ? "gained" : "lost")}; ownerClientId={OwnerClientId}, cursor={Cursor.lockState}, cursorVisible={Cursor.visible}, networkConnected={NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient}.");
    }

    private void OnDisable()
    {
        if (IsSpawned && IsOwner)
            Debug.LogWarning($"[InputDiag][Player] PlayerController component disabled while local player is spawned; ownerClientId={OwnerClientId}.");
    }

    private void OnEnable()
    {
        if (IsSpawned && IsOwner)
            Debug.Log($"[InputDiag][Player] PlayerController component enabled while local player is spawned; ownerClientId={OwnerClientId}.");
    }

    private void Update()
    {
        if (!IsSpawned) return;
        if (!IsOwner || lookAction == null) return;

        // Tab frees the mouse (for the Disconnect button) and locks it again.
        if (Keyboard.current != null && Keyboard.current.tabKey.wasPressedThisFrame)
        {
            bool lockIt = Cursor.lockState != CursorLockMode.Locked;
            Cursor.lockState = lockIt ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !lockIt;
        }
        LogCursorStateIfChanged();

        if (previousWeaponAction != null && previousWeaponAction.WasPressedThisFrame())
            Debug.Log($"[InputDiag][Player] Previous weapon-switch action received; ownerClientId={OwnerClientId}, actionEnabled={previousWeaponAction.enabled}. This component does not implement weapon switching.");
        if (nextWeaponAction != null && nextWeaponAction.WasPressedThisFrame())
            Debug.Log($"[InputDiag][Player] Next weapon-switch action received; ownerClientId={OwnerClientId}, actionEnabled={nextWeaponAction.enabled}. This component does not implement weapon switching.");

        // No steering or jumping while dead or while the cursor is free. Gravity still applies.
        bool dead = health != null && health.IsDead;
        bool allowInput = !dead && Cursor.lockState == CursorLockMode.Locked;

        if (allowInput) HandleLook();
        HandleMovement(allowInput);
    }

    private void HandleLook()
    {
        Vector2 look = lookAction.ReadValue<Vector2>() * mouseSensitivity;

        transform.Rotate(0f, look.x, 0f);
        pitch = Mathf.Clamp(pitch - look.y, -maxPitch, maxPitch);
        cameraRoot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    private void HandleMovement(bool allowInput)
    {
        Vector2 input = allowInput ? moveAction.ReadValue<Vector2>() : Vector2.zero;
        float speed = allowInput && sprintAction.IsPressed() ? runSpeed : walkSpeed;

        Vector3 move = (transform.right * input.x + transform.forward * input.y);
        move = Vector3.ClampMagnitude(move, 1f) * speed;

        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;

        if (allowInput && controller.isGrounded && jumpAction.WasPressedThisFrame())
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);

        verticalVelocity += gravity * Time.deltaTime;
        move.y = verticalVelocity;

        controller.Move(move * Time.deltaTime);
    }

    private void LogCursorStateIfChanged()
    {
        if (_lastLoggedCursorLockState == Cursor.lockState &&
            _lastLoggedCursorVisibility == Cursor.visible)
            return;

        _lastLoggedCursorLockState = Cursor.lockState;
        _lastLoggedCursorVisibility = Cursor.visible;
        Debug.Log($"[InputDiag][Player] Cursor state changed; ownerClientId={OwnerClientId}, cursor={Cursor.lockState}, cursorVisible={Cursor.visible}, focus={Application.isFocused}.");
    }
}