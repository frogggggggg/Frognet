using UnityEditor;
using UnityEditor.UI;

[CustomEditor(typeof(TweenedGridLayoutGroup), true)]
[CanEditMultipleObjects]
public class TweenedGridLayoutGroupEditor : GridLayoutGroupEditor
{
    private SerializedProperty tweenLayout;
    private SerializedProperty tweenDuration;
    private SerializedProperty tweenEase;
    private SerializedProperty tweenUnscaledTime;

    protected override void OnEnable()
    {
        base.OnEnable();
        tweenLayout = serializedObject.FindProperty("tweenLayout");
        tweenDuration = serializedObject.FindProperty("tweenDuration");
        tweenEase = serializedObject.FindProperty("tweenEase");
        tweenUnscaledTime = serializedObject.FindProperty("tweenUnscaledTime");
    }

    public override void OnInspectorGUI()
    {
        base.OnInspectorGUI();

        serializedObject.Update();

        EditorGUILayout.Space(6);
        EditorGUILayout.PropertyField(tweenLayout);

        if (tweenLayout.boolValue)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(tweenDuration);
            EditorGUILayout.PropertyField(tweenEase);
            EditorGUILayout.PropertyField(tweenUnscaledTime);
            EditorGUI.indentLevel--;
        }

        serializedObject.ApplyModifiedProperties();
    }
}
