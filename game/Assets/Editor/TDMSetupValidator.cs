#if UNITY_EDITOR
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > TDM > Validate Setup
/// 1) Select the PLAYER ROOT (in the Hierarchy, in Prefab Mode, or the prefab in the Project window).
/// 2) Open your game scene.
/// 3) Click the menu item. Read the Console: red = must fix, yellow = should check, green = good.
/// </summary>
public static class TDMSetupValidator
{
    private static int _errors, _warnings;

    private static void Err(string m)  { Debug.LogError("[TDM CHECK] FIX: " + m); _errors++; }
    private static void Warn(string m) { Debug.LogWarning("[TDM CHECK] CHECK: " + m); _warnings++; }
    private static void Ok(string m)   { Debug.Log("<color=#4caf50>[TDM CHECK] OK: " + m + "</color>"); }

    // Fields that are allowed to stay empty.
    private static readonly HashSet<string> Optional = new HashSet<string>
        { "impactVFXPrefab", "scorchDecalPrefab", "tracerPrefab", "muzzleFlash", "m_Script" };

    [MenuItem("Tools/TDM/Validate Setup")]
    private static void Run()
    {
        _errors = 0; _warnings = 0;

        CheckProjectSettings();
        CheckScriptVersions();

        GameObject player = Selection.activeGameObject;
        if (player == null) Err("Nothing selected. Select the Player ROOT object (or the Player prefab in the Project window) and run again.");
        else CheckPlayer(player);

        CheckScene();

        if (_errors == 0 && _warnings == 0) Debug.Log("<color=#4caf50><b>[TDM CHECK] ALL GOOD. Setup is complete.</b></color>");
        else Debug.Log($"[TDM CHECK] Finished: {_errors} to fix, {_warnings} to check.");
    }

    // ------------------------------------------------------------- PROJECT
    private static void CheckProjectSettings()
    {
#if ENABLE_LEGACY_INPUT_MANAGER && !ENABLE_INPUT_SYSTEM
        Err("Active Input Handling is the OLD system. Edit > Project Settings > Player > Other Settings > Active Input Handling = Input System Package (New).");
#elif ENABLE_LEGACY_INPUT_MANAGER && ENABLE_INPUT_SYSTEM
        Warn("Active Input Handling is 'Both'. Set it to 'Input System Package (New)' to avoid old-input errors.");
#else
        Ok("Input handling = new Input System.");
#endif
    }

    // Makes sure the scripts in the Assets folder are the NEW versions, not old copies.
    private static void CheckScriptVersions()
    {
        bool ok = true;
        if (typeof(WeaponController).GetField("roundsPerMinute") == null ||
            typeof(WeaponController).GetField("friendlyFire") == null)
        { Err("WeaponController.cs is an OLD version. Replace it with the latest file (needs roundsPerMinute + friendlyFire)."); ok = false; }

        if (typeof(Health).GetEvent("OnDied") == null)
        { Err("Health.cs is an OLD version. Replace it with the latest file (needs the OnDied event)."); ok = false; }

        if (typeof(PlayerTeam).GetField("respawnDelay") == null)
        { Err("PlayerTeam.cs is an OLD version."); ok = false; }

        if (ok) Ok("Core scripts are the latest versions.");
    }

    // -------------------------------------------------------------- PLAYER
    private static void CheckPlayer(GameObject go)
    {
        Debug.Log($"[TDM CHECK] Checking player: {go.name}");

        Need<NetworkObject>(go, "NetworkObject");
        Need<NetworkTransform>(go, "Client Network Transform");
        Need<CharacterController>(go, "CharacterController");
        Need<CapsuleCollider>(go, "Capsule Collider (the hitbox - without it bullets pass through players)");
        NeedByName(go, "PlayerController");
        NeedByName(go, "Crosshair");
        Need<WeaponController>(go, "WeaponController");
        Need<Health>(go, "Health");
        Need<PlayerTeam>(go, "PlayerTeam");
        Need<PlayerAnimatorDriver>(go, "PlayerAnimatorDriver");
        Need<PlayerViewSetup>(go, "PlayerViewSetup");

        // Empty slots in every script on the player.
        int emptySlots = 0;
        foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) { Err("A component on the player has a MISSING script (shows 'Missing Script'). Remove it."); continue; }
            string t = mb.GetType().Name;
            if (t != "PlayerController" && t != "WeaponController" && t != "PlayerViewSetup" &&
                t != "PlayerAnimatorDriver" && t != "ViewModelMotion") continue;

            var so = new SerializedObject(mb);
            var it = so.GetIterator();
            while (it.NextVisible(true))
            {
                if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                if (Optional.Contains(it.name)) continue;
                if (it.objectReferenceValue == null)
                {
                    Err($"{t} on '{mb.gameObject.name}': slot '{it.displayName}' is EMPTY.");
                    emptySlots++;
                }
            }
        }
        if (emptySlots == 0) Ok("No empty slots in the player scripts.");

        // Animators
        var animators = go.GetComponentsInChildren<Animator>(true);
        if (animators.Length == 0) Err("No Animator found. The Mixamo model (Ch15_nonPBR) should have one.");
        if (animators.Length > 1)
            foreach (var a in animators)
                if (a.gameObject == go) Warn("There is an extra Animator on the Player ROOT. Remove it; only the model (Ch15_nonPBR) should have one.");
        foreach (var a in animators)
        {
            if (a.gameObject == go) continue;
            if (a.runtimeAnimatorController == null) Err("Animator on '" + a.name + "' has no Controller. Assign Player_Rifle.");
            if (a.avatar == null || !a.avatar.isHuman) Err("Animator on '" + a.name + "' needs a HUMANOID Avatar (select the FBX > Rig tab > Animation Type = Humanoid).");
            if (a.applyRootMotion) Warn("Animator on '" + a.name + "': turn Apply Root Motion OFF.");

            // The controller must contain the parameters PlayerAnimatorDriver writes to.
            if (a.runtimeAnimatorController is UnityEditor.Animations.AnimatorController ac)
            {
                foreach (string req in new[] { "Speed", "MoveX", "MoveZ", "Grounded", "Jump" })
                {
                    bool found = false;
                    foreach (var prm in ac.parameters) if (prm.name == req) { found = true; break; }
                    if (!found) Err($"Animator Controller '{ac.name}' has no parameter '{req}'. Build a new one: Tools > TDM > Build Rifle Animator Controller.");
                }
            }
        }

        // Cameras
        if (go.GetComponentInChildren<Camera>(true) == null) Err("No Camera under the Player (CameraRoot > Camera).");
        if (go.GetComponentInChildren<AudioListener>(true) == null) Err("No AudioListener on the player's Camera.");

        var hb = go.GetComponent<CapsuleCollider>();
        if (hb != null && hb.isTrigger) Err("The Capsule Collider on the Player root must have Is Trigger OFF.");
        if (hb != null && (hb.height < 1.5f || hb.radius > 0.6f)) Warn("Capsule Collider size looks odd. Recommended: Height 1.8, Radius 0.4, Center (0,0,0).");

        var cc = go.GetComponent<CharacterController>();
        if (cc != null && (cc.height < 1.5f || cc.radius > 0.6f)) Warn("CharacterController size looks odd. Recommended: Height 1.8, Radius 0.4, Center (0,0,0).");
    }

    private static void Need<T>(GameObject go, string label) where T : Component
    {
        if (go.GetComponent<T>() == null) Err($"Player root is missing the '{label}' component.");
    }

    private static void NeedByName(GameObject go, string typeName)
    {
        if (go.GetComponent(typeName) == null) Err($"Player root is missing the '{typeName}' component.");
    }

    // --------------------------------------------------------------- SCENE
    private static void CheckScene()
    {
        var nm = Object.FindFirstObjectByType<NetworkManager>();
        if (nm == null)
        {
            Err("No NetworkManager in the open scene.");
        }
        else
        {
            if (nm.NetworkConfig.PlayerPrefab == null) Err("NetworkManager > Player Prefab is EMPTY. Drag the Player prefab in.");
            if (nm.GetComponent<UnityTransport>() == null) Err("NetworkManager object has no UnityTransport component.");
            if (nm.GetComponent<NetworkBootstrap>() == null) Err("NetworkManager object has no NetworkBootstrap component.");
            if (nm.NetworkConfig.PlayerPrefab != null && nm.NetworkConfig.PlayerPrefab.GetComponent<NetworkObject>() == null)
                Err("The Player Prefab assigned to NetworkManager has no NetworkObject.");
        }

        var points = Object.FindObjectsByType<TeamSpawnPoint>(FindObjectsSortMode.None);
        int a = 0, b = 0;
        foreach (var p in points) { if (p.team == 0) a++; else b++; }
        if (a == 0) Err("No Team A spawn points (TeamSpawnPoint with Team = 0).");
        if (b == 0) Err("No Team B spawn points (TeamSpawnPoint with Team = 1).");
        if (a > 0 && b > 0 && (a < 3 || b < 3)) Warn($"Only {a} Team A and {b} Team B spawn points. 3+ per team is recommended for 5v5.");
        if (a > 0 && b > 0) Ok($"Spawn points: Team A = {a}, Team B = {b}.");

        int cams = 0;
        foreach (var c in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            if (c.gameObject.scene.IsValid() && c.GetComponentInParent<NetworkObject>() == null) cams++;
        if (cams > 0) Warn("There is a Camera in the scene that is not part of the Player. Delete the scene 'Main Camera' (it conflicts with player cameras and creates a 2nd AudioListener).");

        foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            if (mb != null && mb.GetType().Name == "TargetHealth")
                Warn($"Old 'TargetHealth' script found on '{mb.name}'. Remove it and use the 'Health' script instead.");
    }
}
#endif