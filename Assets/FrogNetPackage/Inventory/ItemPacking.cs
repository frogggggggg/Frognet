using PurrNet.Packing;

/// <summary>
/// Sends <see cref="ItemData"/> as its name and resolves it back locally, so the asset reference never
/// travels. PurrNet's codegen picks these up by signature, nothing has to register them.
/// </summary>
public static class ItemPacking
{
    public static void Write(this BitPacker packer, ItemData value)
    {
        packer.Write(value ? value.itemName : string.Empty);
    }

    public static void Read(this BitPacker packer, ref ItemData value)
    {
        string itemName = default;
        packer.Read(ref itemName);
        value = ItemData.Find(itemName);
    }
}
