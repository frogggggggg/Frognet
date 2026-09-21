using UnityEngine;
using DG.Tweening;
using UnityEngine.UI;

public class VirusAccess : MonoBehaviour
{
    public VirusMovement virusMovement;
    public GameObject parentObject;
    public RectTransform drill;
    public float duration;
    public float delay;
    public Ease ease;
    bool opened = false;

    public void OpenUI()
    {
        parentObject.SetActive(true);
        opened = true;

        Renderer rend = virusMovement.cellSpace.GetComponent<Renderer>();
        Bounds worldBounds = rend.bounds;
        Vector3 worldCenter = worldBounds.center;
        Vector3 screenCenter = Camera.main.WorldToScreenPoint(worldCenter);

        Bounds localBounds = rend.localBounds;
        Vector3 scale = rend.transform.lossyScale;

        float radius = Mathf.Max(
            localBounds.extents.x * Mathf.Abs(scale.x),
            localBounds.extents.y * Mathf.Abs(scale.y),
            localBounds.extents.z * Mathf.Abs(scale.z)
        );

        drill.sizeDelta = new Vector2(drill.sizeDelta.x, 0);

        float startY = drill.sizeDelta.y;

        DOVirtual.Float(0, 1, duration, progress => drill.sizeDelta = new Vector2(drill.sizeDelta.x, Mathf.Lerp(startY, Vector3.Distance(screenCenter, Camera.main.WorldToScreenPoint(worldCenter + Camera.main.transform.right * radius))/2, progress)))
            .SetDelay(delay).SetEase(ease);

    }

    void Update()
    {
        if(opened == true){
            Renderer rend = virusMovement.cellSpace.GetComponent<Renderer>();
            Bounds worldBounds = rend.bounds;
            Vector3 worldCenter = worldBounds.center;
            Vector3 screenCenter = Camera.main.WorldToScreenPoint(worldCenter);

            Bounds localBounds = rend.localBounds;
            Vector3 scale = rend.transform.lossyScale;

            float radius = Mathf.Max(
                localBounds.extents.x * Mathf.Abs(scale.x),
                localBounds.extents.y * Mathf.Abs(scale.y),
                localBounds.extents.z * Mathf.Abs(scale.z)
            );
            float diameter = Vector3.Distance(screenCenter, Camera.main.WorldToScreenPoint(worldCenter + Camera.main.transform.right * radius)) * 2;

            parentObject.transform.position = screenCenter;
            parentObject.GetComponent<RectTransform>().sizeDelta = new Vector2(diameter, diameter);
        }
    }
    public void CloseUI()
    {
        opened = false;
        parentObject.SetActive(false);
    }
}
