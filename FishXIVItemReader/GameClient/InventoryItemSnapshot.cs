using System;

namespace FishXIVItemReader.GameClient
{
    [Flags]
    public enum ClientItemFlags : byte
    {
        None = 0,
        HighQuality = 1,
        CompanyCrestApplied = 2,
        Relic = 4,
        Collectable = 8
    }

    public sealed class InventoryItemSnapshot
    {
        public ClientInventoryType Container { get; set; }

        public int Slot { get; set; }

        public uint ItemId { get; set; }

        public int Quantity { get; set; }

        public ushort SpiritbondOrCollectability { get; set; }

        public ushort Condition { get; set; }

        public ClientItemFlags Flags { get; set; }

        public bool IsHighQuality
        {
            get { return (Flags & ClientItemFlags.HighQuality) != 0; }
        }

        public bool IsCollectable
        {
            get { return (Flags & ClientItemFlags.Collectable) != 0; }
        }

        public uint GlamourId { get; set; }

        public ulong Address { get; set; }
    }
}
