using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;
using UnityEngine.InputSystem;
public class Map : MonoBehaviour
{
    public Transform player;
    public Vector2Int mapSize;
    public byte[,] exploreData = new byte[0,0];
    public float exploreRadius = 5;
    private Texture2D explorationTexture;
    public Material mapMaterial;


    public RectTransform MapContainer;
    public RectTransform MapChild;
    public RectTransform Mini, Large;
    public Camera mapCamera;
    public Vector2 CameraSizeChange;
    public Vector2 PPUSizeChange;
    public float time;
    bool isLarge = false;
    public Ease ease;

    public void Swap()
    {
        isLarge = !isLarge;
        MapContainer.DOAnchorMin(isLarge ? Large.anchorMin : Mini.anchorMin, time).SetEase(ease);
        MapContainer.DOAnchorMax(isLarge ? Large.anchorMax : Mini.anchorMax, time).SetEase(ease);
        MapContainer.DOAnchorPos(isLarge ? Large.anchoredPosition : Mini.anchoredPosition, time).SetEase(ease);
        MapContainer.DOSizeDelta(isLarge ? Large.sizeDelta : Mini.sizeDelta, time).SetEase(ease);
        mapCamera.DOOrthoSize(isLarge ? CameraSizeChange.y : CameraSizeChange.x, time).SetEase(ease);
        DOTween.To(
            () => MapContainer.GetComponent<Image>().pixelsPerUnitMultiplier, // Getter
            x => MapContainer.GetComponent<Image>().pixelsPerUnitMultiplier = x, // Setter
            isLarge ? PPUSizeChange.y : PPUSizeChange.x, // End value
            time // Duration
        ).SetEase(ease);
        MapChild.DOScale(isLarge ? Large.localScale : Mini.localScale, time).SetEase(ease);
    }

    void Update()
    {
        Shader.SetGlobalVector("_mapCameraPosition", mapCamera.transform.position);
        Shader.SetGlobalFloat("_mapCameraSize", mapCamera.orthographicSize);
        Shader.SetGlobalVector("_mapSize", (Vector4)(new Vector2(mapSize.x, mapSize.y)));

    }

    void Start()
    {
        exploreData = new byte[mapSize.x, mapSize.y];
        CreateExplorationTexture();
    }

    void FixedUpdate()
    {
        if (player == null)
        {
            player = PlayerManager.TryGetLocal(out var localPlayer) ? localPlayer.transform : null;
            return;
        }
        Vector2Int playerPos = new Vector2Int(Mathf.FloorToInt(player.position.x), Mathf.FloorToInt(player.position.z));
        for (int x = -Mathf.FloorToInt(exploreRadius); x <= Mathf.FloorToInt(exploreRadius); x++)
        {
            for (int y = -Mathf.FloorToInt(exploreRadius); y <= Mathf.FloorToInt(exploreRadius); y++)
            {
                if (x * x + y * y > exploreRadius * exploreRadius) continue;
                int checkX = playerPos.x + x;
                int checkY = playerPos.y + y;
                if (checkX >= 0 && checkX < mapSize.x && checkY >= 0 && checkY < mapSize.y)
                {
                    exploreData[checkX, checkY] = 1;
                }
            }
        }
        UpdateExplorationTexture();
    }

    void CreateExplorationTexture()
    {
        explorationTexture = new Texture2D(
            mapSize.x+2,
            mapSize.y+2,
            TextureFormat.R8,
            false
        );

        explorationTexture.filterMode = FilterMode.Bilinear;
        explorationTexture.wrapMode = TextureWrapMode.Clamp;

        UpdateExplorationTexture();
        if (mapMaterial != null)
        {
            mapMaterial.mainTexture = explorationTexture;
        }
        for (int x = 0; x < mapSize.x+2; x++)
        {
            for (int y = 0; y < mapSize.y+2; y++)
            {
                if(x == 0 || y == 0 || x == mapSize.x+1 || y == mapSize.y+1)
                    explorationTexture.SetPixel(x, y, new Color(0, 0, 0, 1f));
            }
        }
    }

    void UpdateExplorationTexture()
    {
        for (int x = 0; x < mapSize.x; x++)
        {
            for (int y = 0; y < mapSize.y; y++)
            {
                byte value = exploreData[x, y];

                explorationTexture.SetPixel(
                    x+1,
                    y+1,
                    new Color(value, value, value, 1f)
                );
            }
        }

        explorationTexture.Apply();
    }
}
