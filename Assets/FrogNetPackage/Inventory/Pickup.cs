using System.Collections;
using UnityEngine;
using DG.Tweening;

public class Pickup : MonoBehaviour
{
    public enum pickupstyle {
        proximity,
        button,
        click
    }
    public Item item;
    public pickupstyle style;
    public float animateTime;

    [Range(0f, 1f), Tooltip("How fast the item chases the player. Lower is smoother but lags further behind.")]
    public float followSpeed = 0.2f;

    bool interactable = false;

    void OnTriggerEnter(Collider other)
    {
        if(other.CompareTag("Player"))
        {
            if(style == pickupstyle.proximity)
            {
                StartCoroutine(PickThisUp(other.transform));
            }
            if(style == pickupstyle.button)
            {
                interactable = true;
            }
            if(style == pickupstyle.click)
            {
                interactable = true;
            }
        }
    }

    public void Initialize(Item data)
    {
        
    }
    void OnTriggerExit(Collider other)
    {
        if(other.CompareTag("Player"))
        {
            interactable = false;
        }
    }

    IEnumerator PickThisUp(Transform player)
    {
        AnimatePickup(player, animateTime);
        yield return new WaitForSeconds(animateTime);
        Inventory.Instance.SmartAdd(item);
        DestroyThis();
    }

    void DestroyThis()
    {
        Destroy(gameObject);
    }

    /// <summary>
    /// Flies the item to the player. The player's transform only moves on fixed ticks, so the item
    /// chases a trailing point instead of the raw position, which would step visibly.
    /// </summary>
    void AnimatePickup(Transform player, float time)
    {
        Vector3 start = transform.position;
        Vector3 target = player.position;

        DOTween.To(() => 0f, progress =>
            {
                target = Vector3.Lerp(target, player.position, followSpeed);
                transform.position = Vector3.Lerp(start, target, progress);
            }, 1f, time)
            .SetEase(Ease.InElastic)
            .SetLink(gameObject);
    }
}
