using System;
using System.Collections.Generic;

namespace FishXIVItemReader.GameClient
{
    public sealed class InventoryReadResult
    {
        public InventoryReadResult(
            int processId,
            string processName,
            ulong inventoryManagerAddress,
            ulong inventoriesAddress,
            int containersRead,
            int slotsRead,
            IReadOnlyList<InventoryItemSnapshot> items)
            : this(
                processId,
                processName,
                InventoryReadMode.Memory,
                "内存地址",
                inventoryManagerAddress,
                inventoriesAddress,
                containersRead,
                slotsRead,
                items)
        {
        }

        public InventoryReadResult(
            int processId,
            string processName,
            InventoryReadMode readMode,
            string sourceDescription,
            ulong inventoryManagerAddress,
            ulong inventoriesAddress,
            int containersRead,
            int slotsRead,
            IReadOnlyList<InventoryItemSnapshot> items)
        {
            CapturedAt = DateTime.Now;
            ProcessId = processId;
            ProcessName = processName;
            ReadMode = readMode;
            SourceDescription = sourceDescription;
            InventoryManagerAddress = inventoryManagerAddress;
            InventoriesAddress = inventoriesAddress;
            ContainersRead = containersRead;
            SlotsRead = slotsRead;
            Items = items;
        }

        public DateTime CapturedAt { get; }

        public int ProcessId { get; }

        public string ProcessName { get; }

        public InventoryReadMode ReadMode { get; }

        public string SourceDescription { get; }

        public ulong InventoryManagerAddress { get; }

        public ulong InventoriesAddress { get; }

        public int ContainersRead { get; }

        public int SlotsRead { get; }

        public IReadOnlyList<InventoryItemSnapshot> Items { get; }
    }
}
