using UnityEngine;

/// <summary>
/// Build dependencies for everything that's made in code. A build only ships the shaders, compute shaders and
/// prefabs that something in it references, so `Shader.Find` and the bootstraps' prefabs worked in the editor
/// and came back null in a build (no antibodies, legs, chunks, white cells, head view). This asset lives in
/// Resources (always shipped) and references them all; `Shader.Find` then finds them. Add new runtime-only
/// shaders / prefabs here.
/// </summary>
[CreateAssetMenu(menuName = "Viral/Build Assets")]
public class ViralBuildAssets : ScriptableObject
{
    public Shader[] shaders;
    public ComputeShader whiteBloodCellBake;
    [Tooltip("LegSimulation.compute: every SpiderLegWalker's legs (LegRenderer).")]
    public ComputeShader legSimulation;
    [Tooltip("FarField.compute: culls the far field's stand-ins (World/FarField).")]
    public ComputeShader farField;
    public GameObject whiteBloodCells;
    public GameObject resourceField;
    [Tooltip("Spawned into a game scene that has no WorldStreamer. Empty: the scene's own spawners build the world.")]
    public GameObject worldStreamer;

    static ViralBuildAssets s_instance;
    public static ViralBuildAssets Instance => s_instance ? s_instance : s_instance = Resources.Load<ViralBuildAssets>("ViralBuildAssets");

    /// <summary>A copy of a bootstrap prefab (null if it isn't assigned).</summary>
    public static T Spawn<T>(GameObject prefab, string name) where T : Component
    {
        if (!prefab || !prefab.GetComponent<T>()) return null;
        GameObject go = Instantiate(prefab);
        go.name = name;
        return go.GetComponent<T>();
    }
}
