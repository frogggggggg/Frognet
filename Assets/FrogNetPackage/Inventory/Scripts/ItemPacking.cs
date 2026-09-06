using System;
using Frognet.Data;
using PurrNet.Packing;

/// <summary>
/// Sends an item as its registry id, its count, and whatever this copy changed. The values
/// themselves are handled by <see cref="RecordPacking"/>.
/// </summary>
public static class ItemPacking
{
    public static void Write(this BitPacker packer, Item value)
    {
        ushort id = value.id > 0 && value.id <= ushort.MaxValue ? (ushort)value.id : (ushort)0;
        packer.Write(id);

        if (id == 0)
            return;

        packer.Write((ushort)Math.Max(0, Math.Min(value.quantity, ushort.MaxValue)));
        packer.Write(value.overrides);
    }

    public static void Read(this BitPacker packer, ref Item value)
    {
        ushort id = 0;
        packer.Read(ref id);

        if (id == 0)
        {
            value = default;
            return;
        }

        ushort quantity = 0;
        packer.Read(ref quantity);

        Record overrides = default;
        packer.Read(ref overrides);

        value = new Item(id, quantity, overrides);
    }
}
