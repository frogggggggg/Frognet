using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

[AddComponentMenu("Layout/Tweened Grid Layout Group")]
public class TweenedGridLayoutGroup : GridLayoutGroup
{
    public bool tweenLayout = true;
    public float tweenDuration = 0.25f;
    public Ease tweenEase = Ease.OutQuad;
    public bool tweenUnscaledTime = true;

    private readonly Dictionary<RectTransform, Vector2> visualPositions = new Dictionary<RectTransform, Vector2>();
    private readonly Dictionary<RectTransform, Tween> activeTweens = new Dictionary<RectTransform, Tween>();
    private readonly List<RectTransform> stale = new List<RectTransform>();

    public override void SetLayoutVertical()
    {
        base.SetLayoutVertical();
        TweenToLayout();
    }

    protected override void OnDisable()
    {
        KillAllTweens();
        visualPositions.Clear();
        base.OnDisable();
    }

    private void TweenToLayout()
    {
        PruneMissing();

        bool animate = Application.isPlaying && tweenLayout && tweenDuration > 0f;

        for (int i = 0; i < rectChildren.Count; i++)
        {
            RectTransform child = rectChildren[i];
            Vector2 target = child.anchoredPosition;

            KillTween(child);

            if (!animate || !visualPositions.TryGetValue(child, out Vector2 current) || current == target)
            {
                visualPositions[child] = target;
                continue;
            }

            child.anchoredPosition = current;

            RectTransform captured = child;
            activeTweens[captured] = DOTween.To(
                    () => captured.anchoredPosition,
                    position =>
                    {
                        captured.anchoredPosition = position;
                        visualPositions[captured] = position;
                    },
                    target,
                    tweenDuration)
                .SetEase(tweenEase)
                .SetUpdate(tweenUnscaledTime)
                .SetTarget(captured)
                .OnKill(() => activeTweens.Remove(captured));
        }
    }

    private void PruneMissing()
    {
        if (visualPositions.Count == 0)
            return;

        stale.Clear();

        foreach (var pair in visualPositions)
        {
            if (pair.Key == null || pair.Key.parent != transform)
                stale.Add(pair.Key);
        }

        for (int i = 0; i < stale.Count; i++)
        {
            KillTween(stale[i]);
            visualPositions.Remove(stale[i]);
        }

        stale.Clear();
    }

    private void KillTween(RectTransform child)
    {
        if (!activeTweens.TryGetValue(child, out Tween tween))
            return;

        activeTweens.Remove(child);

        if (tween != null && tween.IsActive())
            tween.Kill();
    }

    private void KillAllTweens()
    {
        foreach (var tween in activeTweens.Values)
        {
            if (tween != null && tween.IsActive())
                tween.Kill();
        }

        activeTweens.Clear();
    }
}
