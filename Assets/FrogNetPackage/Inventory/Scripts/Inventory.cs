using System;
using UnityEngine;
using PurrNet;
using UnityEngine.InputSystem;

public class Inventory : NetworkBehaviour
{
    /// <summary>The local player's inventory. Other players' inventories exist too, but are not this.</summary>
    public static Inventory Instance { get; private set; }

    [SerializeField] private SyncArray<Item> slots = new SyncArray<Item>(20);

    public int SlotCount => slots.Length;

    /// <summary>Fires on every client whenever any slot changes, so UI can rebind without polling.</summary>
    public event SyncArray<Item>.SyncArrayChanged<Item> onSlotsChanged
    {
        add => slots.onChanged += value;
        remove => slots.onChanged -= value;
    }

    protected override void OnSpawned()
    {
        if (isOwner)
            Instance = this;
    }

    protected override void OnDespawned()
    {
        if (Instance == this)
            Instance = null;
    }

    public Item GetSlot(int index)
    {
        return index >= 0 && index < slots.Length ? slots[index] : default;
    }

    void Update()
    {
        if (!isOwner)
            return;

        var drop = InputSystem.actions?.FindAction("drop");

        if (drop != null && drop.WasPressedThisFrame())
            Drop(0);
    }

    /// <summary>
    /// Server only. Takes up to <paramref name="removeQuantity"/> from a slot.
    /// Returns what was actually taken, or an empty item if nothing was.
    /// </summary>
    public Item Remove(int index, int removeQuantity = int.MaxValue)
    {
        if (index < 0 || index >= slots.Length || removeQuantity <= 0 || slots[index].IsEmpty)
            return default;

        Item slot = slots[index];
        Item taken = slot;
        taken.quantity = Math.Min(removeQuantity, slot.quantity);

        slot.quantity -= taken.quantity;
        slots[index] = slot.IsEmpty ? default : slot;

        return taken;
    }

    public void Drop(int index, int removeQuantity = 1)
    {
        ServerDrop(index, removeQuantity);
    }

    [ServerRpc(requireOwnership: true)]
    void ServerDrop(int index, int removeQuantity)
    {
        GameObject itemPrefab = Command.Instance ? Command.Instance.itemPrefab : null;

        if (!itemPrefab)
            return;

        // Remove is the validation: it bounds-checks and clamps to what the slot actually holds.
        Item item = Remove(index, removeQuantity);

        if (item.IsEmpty)
            return;

        GameObject spawnedItem = Instantiate(itemPrefab, transform.position, Quaternion.identity);
        spawnedItem.GetComponent<Pickup>().Initialize(item);
    }


    /// <summary>
    /// Two items merge only if they are the same definition and carry the same modifications.
    /// </summary>
    private static bool SameKind(Item a, Item b)
    {
        return a.data == b.data && Item.SameMods(a, b);
    }

    /// <summary>
    /// Returns the index of the first empty slot, or -1 if the inventory is full.
    /// </summary>
    public int FindEmpty()
    {
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].IsEmpty)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Returns the index of the first slot holding a matching item that still has
    /// room to stack, or -1 if there is none.
    /// </summary>
    public int FindSame(Item item)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            if (!slots[i].IsEmpty && slots[i].quantity < slots[i].data.maxStack && SameKind(slots[i], item))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Server only. Adds an item into one specific slot.
    /// Returns the quantity that did not fit (0 means everything was added).
    /// </summary>
    public int Add(int index, Item item)
    {
        if (item.IsEmpty)
            return 0;

        if (index < 0 || index >= slots.Length)
            return item.quantity;

        Item slot = slots[index];

        if (slot.IsEmpty)
            slot = new Item { data = item.data, mods = item.mods, quantity = 0 };
        else if (!SameKind(slot, item))
            return item.quantity;

        int moved = Math.Min(item.data.maxStack - slot.quantity, item.quantity);

        if (moved <= 0)
            return item.quantity;

        slot.quantity += moved;
        slots[index] = slot;
        return item.quantity - moved;
    }

    /// <summary>
    /// Server only. Adds an item anywhere it fits, filling existing stacks before using empty slots.
    /// Returns the quantity that did not fit (0 means everything was added).
    /// </summary>
    public int SmartAdd(Item item)
    {
        if (item.IsEmpty)
            return 0;

        int remaining = item.quantity;

        while (remaining > 0)
        {
            int index = FindSame(item);

            if (index < 0)
                index = FindEmpty();

            if (index < 0)
                break;

            item.quantity = remaining;
            int left = Add(index, item);

            // A slot that accepts nothing would otherwise spin forever.
            if (left >= remaining)
                break;

            remaining = left;
        }

        return remaining;
    }
}
