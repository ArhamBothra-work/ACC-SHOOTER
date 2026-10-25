#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Tools > TDM > Build Rifle Animator Controller
/// Drag in your 4 Mixamo clips, click the button, get a ready Animator Controller
/// with the exact parameters PlayerAnimatorDriver expects.
/// </summary>
public class TDMAnimatorBuilder : EditorWindow
{
    private AnimationClip idle, walk, run, jump;
    private float walkSpeed = 5f;   // = PlayerController Walk Speed
    private float runSpeed = 8f;    // = PlayerController Run Speed
    private string savePath = "Assets/CharacterAnimations/Player_Rifle.controller";

    [MenuItem("Tools/TDM/Build Rifle Animator Controller")]
    private static void Open() => GetWindow<TDMAnimatorBuilder>("Rifle Animator");

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Expand each Mixamo FBX in the Project window (little arrow) and drag the CLIP inside it " +
            "(the one with the play icon, usually named 'mixamo.com') into the boxes below.", MessageType.Info);

        idle = (AnimationClip)EditorGUILayout.ObjectField("Rifle Idle", idle, typeof(AnimationClip), false);
        walk = (AnimationClip)EditorGUILayout.ObjectField("Rifle Walk", walk, typeof(AnimationClip), false);
        run  = (AnimationClip)EditorGUILayout.ObjectField("Rifle Run", run, typeof(AnimationClip), false);
        jump = (AnimationClip)EditorGUILayout.ObjectField("Rifle Jump", jump, typeof(AnimationClip), false);

        walkSpeed = EditorGUILayout.FloatField("Walk Speed", walkSpeed);
        runSpeed = EditorGUILayout.FloatField("Run Speed", runSpeed);
        savePath = EditorGUILayout.TextField("Save Path", savePath);

        GUI.enabled = idle && walk && run && jump;
        if (GUILayout.Button("Build Controller", GUILayout.Height(32))) Build();
        GUI.enabled = true;
    }

    private void Build()
    {
        foreach (var c in new[] { idle, walk, run })
            if (!c.isLooping)
                Debug.LogWarning($"[TDM] Clip '{c.name}' is NOT set to Loop Time. Select its FBX > Animation tab > tick Loop Time > Apply.");

        string folder = System.IO.Path.GetDirectoryName(savePath);
        if (!AssetDatabase.IsValidFolder(folder))
        {
            string parent = System.IO.Path.GetDirectoryName(folder).Replace('\\', '/');
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
        }
        AssetDatabase.DeleteAsset(savePath);

        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(savePath);
        ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("MoveX", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("MoveZ", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("Grounded", AnimatorControllerParameterType.Bool);
        ctrl.AddParameter("Jump", AnimatorControllerParameterType.Trigger);

        var sm = ctrl.layers[0].stateMachine;

        AnimatorState loco = ctrl.CreateBlendTreeInController("Locomotion", out BlendTree tree);
        tree.blendType = BlendTreeType.Simple1D;
        tree.blendParameter = "Speed";
        tree.useAutomaticThresholds = false;
        tree.AddChild(idle, 0f);
        tree.AddChild(walk, walkSpeed);
        tree.AddChild(run, runSpeed);
        sm.defaultState = loco;

        AnimatorState jumpState = sm.AddState("Jump");
        jumpState.motion = jump;

        var toJump = sm.AddAnyStateTransition(jumpState);
        toJump.hasExitTime = false;
        toJump.duration = 0.1f;
        toJump.canTransitionToSelf = false;
        toJump.AddCondition(AnimatorConditionMode.If, 0, "Jump");

        var back = jumpState.AddTransition(loco);
        back.hasExitTime = true;
        back.exitTime = 0.85f;
        back.duration = 0.15f;
        back.AddCondition(AnimatorConditionMode.If, 0, "Grounded");

        EditorUtility.SetDirty(ctrl);
        AssetDatabase.SaveAssets();
        Selection.activeObject = ctrl;
        EditorGUIUtility.PingObject(ctrl);
        Debug.Log($"[TDM] Animator Controller created at {savePath}");
    }
}
#endif
