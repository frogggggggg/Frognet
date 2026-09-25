using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Radial shape maps of objects, for anything that has to wrap round a thing's real mesh (a white cell's lips
/// closing over its catch). Per object, a slot holds the *farthest* surface distance from a centre in every
/// direction (its star-shaped outer hull), as a dual-paraboloid pair: two slices of one Tex2DArray (RHalf),
/// one per hemisphere of world +Z / -Z. The object's own renderers are drawn with Hidden/ShrinkWrapCapture
/// (BlendOp Max, no depth), which places each vertex by the same paraboloid mapping the reader uses
/// (<c>ShrinkWrap.hlsl</c>), so writer and reader can't disagree about orientation. Each hemisphere takes
/// triangles out to 53° past its edge and drops any triangle touching the far pole, so every triangle lands
/// whole in at least one map.
/// Cost: per captured object per frame, 2 x its renderers' draws into a 48x48 slice. Only objects being
/// wrapped are captured (a handful), and the renderer lists are cached per object.
/// </summary>
public sealed class ShrinkWrap : IDisposable
{
    public const int Slots = 8, Size = 48;

    public RenderTexture Maps { get; private set; }

    readonly Material _material;
    readonly CommandBuffer _cmd = new CommandBuffer { name = "ShrinkWrap" };
    readonly Dictionary<Transform, Renderer[]> _renderers = new Dictionary<Transform, Renderer[]>();
    readonly Transform[] _slotOf = new Transform[Slots];
    readonly bool[] _used = new bool[Slots];
    static readonly int CentreId = Shader.PropertyToID("_WrapCentre");
    static readonly List<Renderer> s_found = new List<Renderer>();

    public ShrinkWrap(Shader capture)
    {
        if (capture) _material = new Material(capture) { name = "ShrinkWrap Capture", hideFlags = HideFlags.DontSave };
        Maps = new RenderTexture(Size, Size, 0, RenderTextureFormat.RHalf)
        {
            dimension = TextureDimension.Tex2DArray, volumeDepth = Slots * 2, useMipMap = false,
            filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "ShrinkWrap Maps", hideFlags = HideFlags.DontSave,
        };
        Maps.Create();
    }

    /// <summary>Start a frame's captures.</summary>
    public void Begin()
    {
        _cmd.Clear();
        Array.Clear(_used, 0, Slots);
    }

    /// <summary>Captures 'target' (its renderers no bigger than 'maxExtent' across, so a trailing rope doesn't
    /// count) round 'centre'. Returns its slot, or -1 when out of slots or it has nothing to draw.</summary>
    public int Capture(Transform target, Vector3 centre, float maxExtent)
    {
        if (!_material || !target) return -1;
        int slot = Array.IndexOf(_slotOf, target);
        if (slot < 0 || _used[slot])
        {
            slot = -1;
            for (int i = 0; i < Slots && slot < 0; i++) if (!_used[i] && !_slotOf[i]) slot = i;
            for (int i = 0; i < Slots && slot < 0; i++) if (!_used[i]) slot = i;
            if (slot < 0) return -1;
            _slotOf[slot] = target;
        }
        _used[slot] = true;

        if (!_renderers.TryGetValue(target, out Renderer[] renderers))
        {
            target.GetComponentsInChildren(s_found);
            s_found.RemoveAll(r => !(r is MeshRenderer || r is SkinnedMeshRenderer));
            _renderers[target] = renderers = s_found.ToArray();
        }

        bool any = false;
        for (int side = 0; side < 2; side++)
        {
            _cmd.SetRenderTarget(Maps, 0, CubemapFace.Unknown, slot * 2 + side);
            _cmd.ClearRenderTarget(false, true, Color.clear);
            _cmd.SetGlobalVector(CentreId, new Vector4(centre.x, centre.y, centre.z, side == 0 ? 1f : -1f));
            foreach (Renderer r in renderers)
            {
                if (!r || !r.enabled || !r.gameObject.activeInHierarchy || r.bounds.extents.magnitude > maxExtent) continue;
                int subs = r is SkinnedMeshRenderer sk ? (sk.sharedMesh ? sk.sharedMesh.subMeshCount : 0)
                         : r.TryGetComponent(out MeshFilter mf) && mf.sharedMesh ? mf.sharedMesh.subMeshCount : 0;
                for (int s = 0; s < subs; s++) _cmd.DrawRenderer(r, _material, s, 0);
                any = true;
            }
        }
        return any ? slot : -1;
    }

    /// <summary>Renders this frame's captures now (before the cameras) and forgets objects no longer wrapped.</summary>
    public void Submit()
    {
        Graphics.ExecuteCommandBuffer(_cmd);
        for (int i = 0; i < Slots; i++)
            if (!_used[i] && !ReferenceEquals(_slotOf[i], null)) // destroyed ones too
            {
                _renderers.Remove(_slotOf[i]);
                _slotOf[i] = null;
            }
    }

    public void Dispose()
    {
        _cmd.Release();
        if (Maps) { Maps.Release(); UnityEngine.Object.Destroy(Maps); }
        if (_material) UnityEngine.Object.Destroy(_material);
    }
}
