using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;
using UnityEngine.InputSystem;

public class Map : MonoBehaviour
{
    public Transform player;
    public Vector2Int mapSize;
    public byte[,] exploreData = new byte[0, 0];
    public float exploreRadius = 5;
    public float revealSpeed = 5;

    Texture2D explorationTexture;
    float[,] visualData;

    public Material mapMaterial;

    public RectTransform MapContainer;
    public RectTransform MapChild;
    public float minimapSize = 300;
    public float minimapPadding = 20;

    public Camera mapCamera;
    public Transform mapRotation;

    public Vector2 CameraSizeChange;
    public Vector2 PPUSizeChange;
    public float time;
    public Ease ease;

    bool isLarge;
    bool targetLarge;
    bool swapping;
    Sequence swapTween;

    public float zoomSpeed = 10;
    public float smoothSpeed = 10;
    public Vector2 cameraSizeLimits = new Vector2(5, 20);
    public float clampElasticity = .5f;
    public float dragSpeed = .85f;

    float targetOrthographicSize;
    float mapAngle;

    void Start()
    {
        exploreData = new byte[mapSize.x, mapSize.y];
        visualData = new float[mapSize.x, mapSize.y];

        CreateExplorationTexture();

        targetOrthographicSize = mapCamera.orthographicSize;
        mapAngle = mapRotation.eulerAngles.y;
        targetLarge = isLarge;
    }

    void Update()
    {
        if (isLarge && !swapping)
        {
            float zoom = InputSystem.actions["zoom"].ReadValue<float>();

            targetOrthographicSize -=
                zoom * zoomSpeed * Time.deltaTime * mapCamera.orthographicSize;

            targetOrthographicSize = ElasticClamp(
                targetOrthographicSize,
                cameraSizeLimits.x,
                cameraSizeLimits.y,
                clampElasticity
            );

            mapCamera.orthographicSize = Mathf.Lerp(
                mapCamera.orthographicSize,
                targetOrthographicSize,
                Time.deltaTime * smoothSpeed
            );

            Vector2 drag = InputSystem.actions["drag"].ReadValue<Vector2>();

            float canvasScale =
                MapChild.GetComponentInParent<Canvas>().scaleFactor;

            float x =
                mapCamera.orthographicSize * 2f * mapCamera.aspect /
                (MapChild.rect.width * canvasScale);

            float y =
                mapCamera.orthographicSize * 2f /
                (MapChild.rect.height * canvasScale);

            mapCamera.transform.position +=
                new Vector3(drag.x * x, 0, drag.y * y) * dragSpeed;
        }
        else if (!swapping)
        {
            mapAngle = mapRotation.eulerAngles.y;
        }

        SmoothTexture();

        Shader.SetGlobalVector(
            "_mapCameraPosition",
            (Vector4)mapCamera.transform.position +
            new Vector4(0, 0, 0, mapAngle)
        );

        Shader.SetGlobalFloat(
            "_mapCameraSize",
            mapCamera.orthographicSize
        );

        Shader.SetGlobalVector(
            "_mapSize",
            (Vector4)new Vector2(mapSize.x, mapSize.y)
        );
    }

    void FixedUpdate()
    {
        if (player == null)
        {
            player = PlayerManager.TryGetLocal(out var localPlayer)
                ? localPlayer.transform
                : null;

            return;
        }

        Vector2Int playerPos = new Vector2Int(
            Mathf.FloorToInt(player.position.x),
            Mathf.FloorToInt(player.position.z)
        );

        int radius = Mathf.FloorToInt(exploreRadius);

        for (int x = -radius; x <= radius; x++)
        {
            for (int y = -radius; y <= radius; y++)
            {
                if (x * x + y * y > exploreRadius * exploreRadius)
                    continue;

                int checkX = playerPos.x + x;
                int checkY = playerPos.y + y;

                if (checkX >= 0 && checkX < mapSize.x &&
                    checkY >= 0 && checkY < mapSize.y)
                {
                    exploreData[checkX, checkY] = 1;
                }
            }
        }
    }

    void CreateExplorationTexture()
    {
        explorationTexture = new Texture2D(
            mapSize.x + 2,
            mapSize.y + 2,
            TextureFormat.R8,
            false
        );

        explorationTexture.filterMode = FilterMode.Bilinear;
        explorationTexture.wrapMode = TextureWrapMode.Clamp;

        for (int x = 0; x < mapSize.x + 2; x++)
            for (int y = 0; y < mapSize.y + 2; y++)
                explorationTexture.SetPixel(x, y, Color.black);

        explorationTexture.Apply();

        if (mapMaterial != null)
            mapMaterial.mainTexture = explorationTexture;
    }

    void SmoothTexture()
    {
        bool changed = false;

        for (int x = 0; x < mapSize.x; x++)
        {
            for (int y = 0; y < mapSize.y; y++)
            {
                float old = visualData[x, y];

                visualData[x, y] = Mathf.MoveTowards(
                    old,
                    exploreData[x, y],
                    revealSpeed * Time.deltaTime
                );

                if (old == visualData[x, y])
                    continue;

                float v = visualData[x, y];

                explorationTexture.SetPixel(
                    x + 1,
                    y + 1,
                    new Color(v, v, v, 1)
                );

                changed = true;
            }
        }

        if (changed)
            explorationTexture.Apply(false);
    }

    public void Swap()
    {
        targetLarge = !targetLarge;
        swapping = true;

        swapTween?.Kill();

        float width = Screen.width - minimapPadding * 2;
        float height = Screen.height - minimapPadding * 2;

        Vector2 containerSize = targetLarge
            ? new Vector2(width, height)
            : Vector2.one * minimapSize;

        float childSize = targetLarge
            ? Mathf.Max(width, height)
            : minimapSize;

        float cameraSize = targetLarge
            ? CameraSizeChange.y
            : CameraSizeChange.x;

        float ppu = targetLarge
            ? PPUSizeChange.y
            : PPUSizeChange.x;

        float startAngle = mapAngle;
        Image image = MapContainer.GetComponent<Image>();

        swapTween = DOTween.Sequence();

        swapTween.Join(MapContainer.DOSizeDelta(containerSize, time));
        swapTween.Join(MapChild.DOSizeDelta(Vector2.one * childSize, time));
        swapTween.Join(mapCamera.DOOrthoSize(cameraSize, time));

        swapTween.Join(
            mapCamera.transform.DOLocalMove(
                new Vector3(0, mapCamera.transform.localPosition.y, 0),
                time
            )
        );

        swapTween.Join(DOVirtual.Float(0, 1, time, t =>
        {
            mapAngle = Mathf.LerpAngle(
                startAngle,
                targetLarge ? 0 : mapRotation.eulerAngles.y,
                t
            );
        }));

        swapTween.Join(DOTween.To(
            () => image.pixelsPerUnitMultiplier,
            x => image.pixelsPerUnitMultiplier = x,
            ppu,
            time
        ));

        targetOrthographicSize = cameraSize;

        swapTween.SetEase(ease).OnComplete(() =>
        {
            isLarge = targetLarge;
            swapping = false;
            mapAngle = isLarge ? 0 : mapRotation.eulerAngles.y;
        });
    }

    public static float ElasticClamp(
        float value,
        float min,
        float max,
        float elasticity = .5f)
    {
        if (value < min)
            return min -
                Mathf.Log(1 + (min - value) * elasticity) / elasticity;

        if (value > max)
            return max +
                Mathf.Sqrt((value - max) * elasticity);

        return value;
    }
}