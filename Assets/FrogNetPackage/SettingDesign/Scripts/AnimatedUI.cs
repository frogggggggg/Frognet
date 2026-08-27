using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;

[RequireComponent(typeof(RectTransform))]
public class AnimatedUI : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [System.Serializable]
    public class Effect
    {
        public bool enabled;
        public float duration = 0.15f;
        public AnimationCurve ease = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        public Vector2 scale = Vector2.one;
        public float shake;
        public float fade = 1f;
    }

    public Effect pointerEnter = new Effect();
    public Effect pointerExit = new Effect();
    public Effect appear = new Effect();
    public Effect destroy = new Effect();
    public bool unscaledTime = true;

    private RectTransform rect;
    private CanvasGroup group;
    private Vector2 restPosition;
    private Vector3 restScale;
    private Coroutine running;

    private void Awake()
    {
        rect = (RectTransform)transform;
        restPosition = rect.anchoredPosition;
        restScale = rect.localScale;

        group = GetComponent<CanvasGroup>();
        if (group == null && NeedsFade())
            group = gameObject.AddComponent<CanvasGroup>();
    }

    private void OnEnable()
    {
        if (!appear.enabled)
            return;

        ApplyState(restScale * appear.scale.x, appear.fade);
        Play(appear, restScale * appear.scale.y, 1f, false);
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (pointerEnter.enabled)
            PlayFromCurrent(pointerEnter, false);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (pointerExit.enabled)
            PlayFromCurrent(pointerExit, false);
        else if (pointerEnter.enabled)
            Play(pointerEnter, restScale, 1f, false);
    }

    public void DestroyAnimate()
    {
        if (!destroy.enabled)
        {
            Destroy(gameObject);
            return;
        }

        PlayFromCurrent(destroy, true);
    }

    private void PlayFromCurrent(Effect effect, bool destroyAtEnd)
    {
        ApplyState(restScale * effect.scale.x, group != null ? group.alpha : 1f);
        Play(effect, restScale * effect.scale.y, effect.fade, destroyAtEnd);
    }

    private void Play(Effect effect, Vector3 targetScale, float targetAlpha, bool destroyAtEnd)
    {
        if (running != null)
        {
            StopCoroutine(running);
            running = null;
        }

        if (!Application.isPlaying || !isActiveAndEnabled || effect.duration <= 0f)
        {
            ApplyState(targetScale, targetAlpha);

            if (destroyAtEnd)
                Destroy(gameObject);

            return;
        }

        running = StartCoroutine(Animate(effect, targetScale, targetAlpha, destroyAtEnd));
    }

    private IEnumerator Animate(Effect effect, Vector3 targetScale, float targetAlpha, bool destroyAtEnd)
    {
        Vector3 startScale = rect.localScale;
        float startAlpha = group != null ? group.alpha : 1f;
        float elapsed = 0f;

        while (elapsed < effect.duration)
        {
            float progress = elapsed / effect.duration;
            float t = effect.ease != null && effect.ease.length > 0 ? effect.ease.Evaluate(progress) : progress;

            if (effect.shake > 0f)
                rect.anchoredPosition = restPosition + Random.insideUnitCircle * (effect.shake * (1f - progress));

            ApplyState(
                Vector3.LerpUnclamped(startScale, targetScale, t),
                Mathf.LerpUnclamped(startAlpha, targetAlpha, t));

            elapsed += unscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
            yield return null;
        }

        if (effect.shake > 0f)
            rect.anchoredPosition = restPosition;

        ApplyState(targetScale, targetAlpha);
        running = null;

        if (destroyAtEnd)
            Destroy(gameObject);
    }

    private void ApplyState(Vector3 scale, float alpha)
    {
        rect.localScale = scale;

        if (group != null)
            group.alpha = alpha;
    }

    private bool NeedsFade()
    {
        return !Mathf.Approximately(pointerEnter.fade, 1f)
            || !Mathf.Approximately(pointerExit.fade, 1f)
            || !Mathf.Approximately(appear.fade, 1f)
            || !Mathf.Approximately(destroy.fade, 1f);
    }
}
