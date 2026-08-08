using System;
using UnityEngine;

public class Inventory : MonoBehaviour
{
    public static Inventory Instance { get; private set; }

    [SerializeField] private Item[] slots = new Item[20];

    public int SlotCount => slots.Length;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    public Item GetSlot(int index)
    {
        return index >= 0 && index < slots.Length ? slots[index] : default;
    }

    /// <summary>
    /// Takes up to <paramref name="removeQuantity"/> from a slot.
    /// Returns what was actually taken, or an empty item if nothing was.
    /// </summary>
    public Item Remove(int index, int removeQuantity = int.MaxValue)
    {
        if (index < 0 || index >= slots.Length || removeQuantity <= 0 || slots[index].IsEmpty)
            return default;

        Item taken = slots[index];
        taken.quantity = Math.Min(removeQuantity, taken.quantity);

        slots[index].quantity -= taken.quantity;
        if (slots[index].IsEmpty)
            slots[index] = default;

        return taken;
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
    /// Adds an item into one specific slot.
    /// Returns the quantity that did not fit (0 means everything was added).
    /// </summary>
    public int Add(int index, Item item)
    {
        if (item.IsEmpty)
            return 0;

        if (index < 0 || index >= slots.Length)
            return item.quantity;

        if (slots[index].IsEmpty)
            slots[index] = new Item { data = item.data, mods = item.mods, quantity = 0 };
        else if (!SameKind(slots[index], item))
            return item.quantity;

        int moved = Math.Min(item.data.maxStack - slots[index].quantity, item.quantity);
        slots[index].quantity += moved;
        return item.quantity - moved;
    }

    /// <summary>
    /// Adds an item anywhere it fits, filling existing stacks before using empty slots.
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
            if (index < 0){
                index = FindEmpty();
            if (index < 0)
                break;
            }

            item.quantity = remaining;
            remaining = Add(index, item);
        }

        return remaining;
    }
}
