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

    void AnimatePickup(Transform player, float time)
    {
        Vector3 start = transform.position;

        DOTween.To(() => 0f, t => transform.position = Vector3.Lerp(start, player.position, t), 1f, time)
            .SetEase(Ease.InOutSine)
            .SetTarget(transform);
    }
}
