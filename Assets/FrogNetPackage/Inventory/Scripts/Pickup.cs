using System.Collections;
using UnityEngine;
using DG.Tweening;
using PurrNet;
using PurrNet.Transports;

/// <remarks>
/// Deliberately has no Rigidbody. PurrDiction sets <c>Physics.simulationMode = Script</c> and only steps
/// the prediction manager's own scene, so an unpredicted rigidbody would be re-stepped on every
/// resimulation and never rolled back. Motion is tweened on the server and carried by NetworkTransform.
/// </remarks>
[RequireComponent(typeof(NetworkTransform))]
public class Pickup : NetworkBehaviour
{
    public enum pickupstyle {
        proximity,
        button,
        click
    }

    [SerializeField] private SyncVar<Item> item = new SyncVar<Item>();
    public pickupstyle style;
    public float animateTime;

    [Range(0f, 1f), Tooltip("How fast the item chases the player. Lower is smoother but lags further behind.")]
    public float followSpeed = 0.2f;

    [Tooltip("Seconds the toss arc takes when spawned with a launch vector.")]
    public float tossTime = 0.6f;

    [Tooltip("What the toss arc is allowed to land on.")]
    public LayerMask groundMask = ~0;

    bool interactable = false;
    bool claimed = false;

    /// <summary>
    /// Server only. Call right after instantiating. <paramref name="launch"/> is a cosmetic arc, not physics:
    /// it only decides where the pickup comes to rest and how it flies there.
    /// </summary>
    public void Initialize(Item newItem, Vector3 launch = default)
    {
        if(!NetworkManager.main || !NetworkManager.main.isServer)
            return;

        item.value = newItem;

        if(launch == Vector3.zero)
            return;

        transform.DOJump(ResolveLanding(transform.position, launch), 1f, 1, tossTime).SetLink(gameObject);
    }

    Vector3 ResolveLanding(Vector3 origin, Vector3 launch)
    {
        Vector3 target = origin + launch * tossTime;

        return gameObject.scene.GetPhysicsScene()
            .Raycast(target + Vector3.up * 2f, Vector3.down, out var hit, 20f, groundMask)
            ? hit.point
            : target;
    }

    void OnTriggerEnter(Collider other)
    {
        if(!IsLocalPlayer(other))
            return;

        if(style == pickupstyle.proximity)
            RequestPickup();
        else
            interactable = true;
    }

    void OnTriggerExit(Collider other)
    {
        if(IsLocalPlayer(other))
        {
            interactable = false;
        }
    }

    /// <summary>Hook for the button and click styles to call once their input fires.</summary>
    public void TryInteract()
    {
        if(interactable)
            RequestPickup();
    }

    static bool IsLocalPlayer(Collider other)
    {
        if(!other.CompareTag("Player"))
            return false;

        var player = other.GetComponentInParent<PlayerManager>();
        return player && player.isOwner;
    }

    [ServerRpc(requireOwnership: false)]
    void RequestPickup(RPCInfo info = default)
    {
        if(claimed)
            return;

        PlayerManager.TryGetPlayer(info.sender, out var player);
        var inventory = player ? player.GetComponentInChildren<Inventory>() : null;

        if(!inventory)
            return;

        int leftover = inventory.SmartAdd(item.value);

        if(leftover > 0)
        {
            // Took what fit; the rest stays on the ground.
            var remainder = item.value;
            remainder.quantity = leftover;
            item.value = remainder;
            return;
        }

        claimed = true;

        AnimatePickup(player.transform, animateTime);
        StartCoroutine(DespawnAfterAnimation());
    }

    protected override void OnPoolReset()
    {
        claimed = false;
        interactable = false;
    }



    IEnumerator DespawnAfterAnimation()
    {
        yield return new WaitForSeconds(animateTime);
        Despawn();
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
