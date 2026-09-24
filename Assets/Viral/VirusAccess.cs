using DG.Tweening;
using UnityEngine;

/// <summary>
/// Screen-space frame that circles the cell the virus is standing on, with a
/// drill bar that grows in when opened. Knows nothing about movement: it asks
/// the virus's ISurfaceContact what it's standing on.
/// </summary>
public class VirusAccess : MonoBehaviour
{
    [Tooltip("The virus: anything with an ISurfaceContact on it or a parent.")]
    public Transform virus;
    [Tooltip("Frame kept centred on the cell and sized to it.")]
    public GameObject parentObject;
    public RectTransform drill;
    public float duration;
    public float delay;
    public Ease ease;

    ISurfaceContact _contact;
    RectTransform _frame;
    Renderer _cell;
    Camera _cam;
    Tween _tween;

    void Awake()
    {
        _contact = virus ? virus.GetComponentInParent<ISurfaceContact>() : null;
        _frame = parentObject.GetComponent<RectTransform>();
    }

    public void OpenUI()
    {
        Transform surface = _contact?.Surface;
        _cell = surface ? surface.GetComponentInChildren<Renderer>() : null;
        _cam = Camera.main;
        if (!_cell || !_cam) return;

        parentObject.SetActive(true);
        float diameter = Frame();

        _tween?.Kill();
        drill.sizeDelta = new Vector2(drill.sizeDelta.x, 0f);
        _tween = DOVirtual.Float(0f, diameter * 0.25f, duration, h => drill.sizeDelta = new Vector2(drill.sizeDelta.x, h))
            .SetDelay(delay)
            .SetEase(ease);
    }

    public void CloseUI()
    {
        _tween?.Kill();
        _cell = null;
        parentObject.SetActive(false);
    }

    void Update()
    {
        if (_cell && _cam) Frame();
    }

    /// <summary>Centre the frame on the cell and size it to the cell's on-screen diameter.</summary>
    float Frame()
    {
        Bounds local = _cell.localBounds;
        Vector3 scale = _cell.transform.lossyScale;
        float radius = Mathf.Max(local.extents.x * Mathf.Abs(scale.x),
                                 local.extents.y * Mathf.Abs(scale.y),
                                 local.extents.z * Mathf.Abs(scale.z));

        Vector3 center = _cell.bounds.center;
        Vector2 screenCenter = _cam.WorldToScreenPoint(center);
        Vector2 screenEdge = _cam.WorldToScreenPoint(center + _cam.transform.right * radius);
        float diameter = Vector2.Distance(screenCenter, screenEdge) * 2f;

        _frame.position = screenCenter;
        _frame.sizeDelta = new Vector2(diameter, diameter);
        return diameter;
    }
}