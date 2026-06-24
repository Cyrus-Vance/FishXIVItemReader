using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace FishXIVItemReader.GameClient
{
    public static class ClientInventoryReader
    {
        private const string GameProcessName = "ffxiv_dx11";
        private const string GameExecutableName = "ffxiv_dx11.exe";

        private const int ProcessVmRead = 0x0010;
        private const int ProcessQueryLimitedInformation = 0x1000;

        private const int MaxContainersToScan = 512;
        private const int MaxContainerSize = 256;

        private const int InventoryManagerInventoriesOffset = 0x1E08;

        private const int InventoryContainerSize = 0x20;
        private const int InventoryContainerItemsOffset = 0x08;
        private const int InventoryContainerTypeOffset = 0x10;
        private const int InventoryContainerSizeOffset = 0x14;
        private const int InventoryContainerIsLoadedOffset = 0x18;

        private const int InventoryItemSize = 0x48;
        private const int InventoryItemContainerOffset = 0x08;
        private const int InventoryItemSlotOffset = 0x0C;
        private const int InventoryItemIsSymbolicOffset = 0x0E;
        private const int InventoryItemIdOffset = 0x10;
        private const int InventoryItemQuantityOffset = 0x14;
        private const int InventoryItemSpiritbondOffset = 0x18;
        private const int InventoryItemConditionOffset = 0x1A;
        private const int InventoryItemFlagsOffset = 0x1C;
        private const int InventoryItemGlamourIdOffset = 0x3C;

        private static readonly byte?[] InventoryManagerInstanceSignature =
        {
            0x48, 0x8D, 0x0D, null, null, null, null, 0x81, 0xC2
        };

        private static readonly object InventoryManagerCacheGate = new object();
        private static readonly Dictionary<int, ulong> InventoryManagerAddressCache = new Dictionary<int, ulong>();

        private static readonly ClientInventoryType[] PlayerInventoryTypes =
        {
            ClientInventoryType.Inventory1,
            ClientInventoryType.Inventory2,
            ClientInventoryType.Inventory3,
            ClientInventoryType.Inventory4
        };

        private static readonly ClientInventoryType[] EquippedInventoryTypes =
        {
            ClientInventoryType.EquippedItems
        };

        private static readonly ClientInventoryType[] SaddleBagInventoryTypes =
        {
            ClientInventoryType.SaddleBag1,
            ClientInventoryType.SaddleBag2,
            ClientInventoryType.PremiumSaddleBag1,
            ClientInventoryType.PremiumSaddleBag2
        };

        private static readonly ClientInventoryType[] ArmoryChestInventoryTypes =
        {
            ClientInventoryType.ArmoryMainHand,
            ClientInventoryType.ArmoryOffHand,
            ClientInventoryType.ArmoryHead,
            ClientInventoryType.ArmoryBody,
            ClientInventoryType.ArmoryHands,
            ClientInventoryType.ArmoryWaist,
            ClientInventoryType.ArmoryLegs,
            ClientInventoryType.ArmoryFeets,
            ClientInventoryType.ArmoryEar,
            ClientInventoryType.ArmoryNeck,
            ClientInventoryType.ArmoryWrist,
            ClientInventoryType.ArmoryRings,
            ClientInventoryType.ArmorySoulCrystal
        };

        private static readonly ClientInventoryType[] RetainerStorageInventoryTypes =
        {
            ClientInventoryType.RetainerPage1,
            ClientInventoryType.RetainerPage2,
            ClientInventoryType.RetainerPage3,
            ClientInventoryType.RetainerPage4,
            ClientInventoryType.RetainerPage5,
            ClientInventoryType.RetainerPage6,
            ClientInventoryType.RetainerPage7
        };

        public static IReadOnlyList<ClientProcessInfo> FindCandidateProcesses()
        {
            return Process.GetProcesses()
                .Where(IsLikelyGameProcess)
                .Select(TryCreateProcessInfo)
                .Where(p => p != null)
                .OrderBy(p => p.ProcessName)
                .ThenBy(p => p.ProcessId)
                .ToList();
        }

        private static bool IsLikelyGameProcess(Process process)
        {
            if (string.Equals(process.ProcessName, GameProcessName, StringComparison.OrdinalIgnoreCase))
                return true;

            try
            {
                var fileName = process.MainModule != null
                    ? Path.GetFileName(process.MainModule.FileName)
                    : string.Empty;

                return string.Equals(fileName, GameExecutableName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static InventoryReadResult ReadPlayerInventory(
            int processId,
            bool includeSaddleBags,
            bool includeRetainerStorage,
            bool includeArmoryChest,
            bool includeEquippedItems)
        {
            if (!Environment.Is64BitProcess)
            {
                throw new NotSupportedException("请使用64位ACT。");
            }

            using (var process = Process.GetProcessById(processId))
            using (var memory = ProcessMemory.Open(processId))
            {
                var inventoryManagerAddress = GetCachedOrLocateInventoryManager(memory, process);
                var inventoriesAddress = memory.ReadUInt64(inventoryManagerAddress + InventoryManagerInventoriesOffset);

                if (!IsLikelyPointer(inventoriesAddress))
                {
                    throw new InvalidOperationException("已定位背包管理器，但容器列表指针无效。请确认角色已经登录进游戏。");
                }

                var requestedTypes = CreateRequestedInventoryTypes(
                    includeSaddleBags,
                    includeRetainerStorage,
                    includeArmoryChest,
                    includeEquippedItems);
                var containers = ScanInventoryContainers(memory, inventoriesAddress, requestedTypes);
                var items = new List<InventoryItemSnapshot>();
                var slotsRead = 0;

                foreach (var inventoryType in requestedTypes)
                {
                    InventoryContainerSnapshot container;
                    if (!containers.TryGetValue(inventoryType, out container))
                    {
                        continue;
                    }

                    slotsRead += container.Size;
                    items.AddRange(ReadContainerItems(memory, container));
                }

                if (containers.Count == 0)
                {
                    throw new InvalidOperationException("没有找到已加载的玩家背包容器。请确认角色已经登录进游戏，并且背包中至少有一个物品。");
                }

                return new InventoryReadResult(
                    process.Id,
                    process.ProcessName,
                    inventoryManagerAddress,
                    inventoriesAddress,
                    containers.Count,
                    slotsRead,
                    items);
            }
        }

        public static InventoryReadResult ReadPlayerInventory(int processId)
        {
            return ReadPlayerInventory(
                processId,
                includeSaddleBags: false,
                includeRetainerStorage: false,
                includeArmoryChest: false,
                includeEquippedItems: false);
        }

        public static InventoryReadResult ReadPlayerInventory(int processId, bool includeSaddleBags)
        {
            return ReadPlayerInventory(
                processId,
                includeSaddleBags,
                includeRetainerStorage: false,
                includeArmoryChest: false,
                includeEquippedItems: false);
        }

        public static InventoryReadResult ReadPlayerInventory(
            int processId,
            bool includeSaddleBags,
            bool includeRetainerStorage)
        {
            return ReadPlayerInventory(
                processId,
                includeSaddleBags,
                includeRetainerStorage,
                includeArmoryChest: false,
                includeEquippedItems: false);
        }

        public static InventoryReadResult ReadPlayerInventory(
            int processId,
            bool includeSaddleBags,
            bool includeRetainerStorage,
            bool includeArmoryChest)
        {
            return ReadPlayerInventory(
                processId,
                includeSaddleBags,
                includeRetainerStorage,
                includeArmoryChest,
                includeEquippedItems: false);
        }

        private static ClientInventoryType[] CreateRequestedInventoryTypes(
            bool includeSaddleBags,
            bool includeRetainerStorage,
            bool includeArmoryChest,
            bool includeEquippedItems)
        {
            var requestedTypes = new List<ClientInventoryType>(PlayerInventoryTypes);

            if (includeEquippedItems)
            {
                requestedTypes.AddRange(EquippedInventoryTypes);
            }

            if (includeSaddleBags)
            {
                requestedTypes.AddRange(SaddleBagInventoryTypes);
            }

            if (includeRetainerStorage)
            {
                requestedTypes.AddRange(RetainerStorageInventoryTypes);
            }

            if (includeArmoryChest)
            {
                requestedTypes.AddRange(ArmoryChestInventoryTypes);
            }

            return requestedTypes.ToArray();
        }

        private static ClientProcessInfo TryCreateProcessInfo(Process process)
        {
            try
            {
                var modulePath = process.MainModule != null ? process.MainModule.FileName : string.Empty;
                return new ClientProcessInfo(process.Id, process.ProcessName, modulePath);
            }
            catch
            {
                return new ClientProcessInfo(process.Id, process.ProcessName, string.Empty);
            }
            finally
            {
                process.Dispose();
            }
        }

        private static ulong GetCachedOrLocateInventoryManager(ProcessMemory memory, Process process)
        {
            ulong cachedAddress;
            lock (InventoryManagerCacheGate)
            {
                if (InventoryManagerAddressCache.TryGetValue(process.Id, out cachedAddress))
                {
                    ulong inventoriesAddress;
                    if (TryReadInventoriesPointer(memory, cachedAddress, out inventoriesAddress))
                    {
                        return cachedAddress;
                    }

                    InventoryManagerAddressCache.Remove(process.Id);
                }
            }

            var locatedAddress = LocateInventoryManager(memory, process);
            lock (InventoryManagerCacheGate)
            {
                InventoryManagerAddressCache[process.Id] = locatedAddress;
            }

            return locatedAddress;
        }

        private static ulong LocateInventoryManager(ProcessMemory memory, Process process)
        {
            var mainModule = process.MainModule;
            if (mainModule == null)
            {
                throw new InvalidOperationException("无法检查FFXIV主模块。");
            }

            var moduleBase = unchecked((ulong)mainModule.BaseAddress.ToInt64());
            var moduleSize = mainModule.ModuleMemorySize;
            var moduleBytes = memory.ReadBytes(moduleBase, moduleSize);
            ulong firstMatch = 0;

            foreach (var matchOffset in FindPattern(moduleBytes, InventoryManagerInstanceSignature))
            {
                var instructionAddress = moduleBase + (ulong)matchOffset;
                var displacement = BitConverter.ToInt32(moduleBytes, matchOffset + 3);
                var candidate = unchecked((ulong)((long)instructionAddress + 7 + displacement));

                if (firstMatch == 0)
                {
                    firstMatch = candidate;
                }

                ulong inventoriesAddress;
                if (TryReadInventoriesPointer(memory, candidate, out inventoriesAddress))
                {
                    var containers = ScanInventoryContainers(memory, inventoriesAddress, PlayerInventoryTypes);
                    if (containers.Count == PlayerInventoryTypes.Length)
                    {
                        return candidate;
                    }
                }
            }

            if (firstMatch != 0)
            {
                return firstMatch;
            }

            throw new InvalidOperationException("无法定位背包管理器实例。");
        }

        private static bool TryReadInventoriesPointer(ProcessMemory memory, ulong inventoryManagerAddress, out ulong inventoriesAddress)
        {
            inventoriesAddress = 0;

            try
            {
                inventoriesAddress = memory.ReadUInt64(inventoryManagerAddress + InventoryManagerInventoriesOffset);
                return IsLikelyPointer(inventoriesAddress);
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<ClientInventoryType, InventoryContainerSnapshot> ScanInventoryContainers(
            ProcessMemory memory,
            ulong inventoriesAddress,
            IReadOnlyCollection<ClientInventoryType> targetTypes)
        {
            var result = new Dictionary<ClientInventoryType, InventoryContainerSnapshot>();
            var targetSet = new HashSet<ClientInventoryType>(targetTypes);

            for (var index = 0; index < MaxContainersToScan; index++)
            {
                var address = inventoriesAddress + (ulong)(index * InventoryContainerSize);
                InventoryContainerSnapshot container;
                if (!TryReadContainer(memory, address, targetSet, out container))
                {
                    continue;
                }

                if (!result.ContainsKey(container.Type))
                {
                    result.Add(container.Type, container);
                }

                if (result.Count == targetSet.Count)
                {
                    break;
                }
            }

            return result;
        }

        private static bool TryReadContainer(
            ProcessMemory memory,
            ulong address,
            HashSet<ClientInventoryType> targetTypes,
            out InventoryContainerSnapshot container)
        {
            container = null;

            byte[] bytes;
            if (!memory.TryReadBytes(address, InventoryContainerSize, out bytes))
            {
                return false;
            }

            var typeValue = BitConverter.ToUInt32(bytes, InventoryContainerTypeOffset);
            var type = (ClientInventoryType)typeValue;
            if (!targetTypes.Contains(type))
            {
                return false;
            }

            var itemsAddress = BitConverter.ToUInt64(bytes, InventoryContainerItemsOffset);
            var size = BitConverter.ToInt32(bytes, InventoryContainerSizeOffset);
            var isLoaded = bytes[InventoryContainerIsLoadedOffset] != 0;

            if (!isLoaded || size <= 0 || size > MaxContainerSize || !IsLikelyPointer(itemsAddress))
            {
                return false;
            }

            container = new InventoryContainerSnapshot
            {
                Address = address,
                ItemsAddress = itemsAddress,
                Type = type,
                Size = size
            };

            return true;
        }

        private static IEnumerable<InventoryItemSnapshot> ReadContainerItems(ProcessMemory memory, InventoryContainerSnapshot container)
        {
            var bytes = memory.ReadBytes(container.ItemsAddress, container.Size * InventoryItemSize);

            for (var index = 0; index < container.Size; index++)
            {
                var offset = index * InventoryItemSize;
                var isSymbolic = bytes[offset + InventoryItemIsSymbolicOffset] != 0;
                if (isSymbolic)
                {
                    continue;
                }

                var itemId = BitConverter.ToUInt32(bytes, offset + InventoryItemIdOffset);
                var quantity = BitConverter.ToInt32(bytes, offset + InventoryItemQuantityOffset);
                if (itemId == 0 || quantity <= 0)
                {
                    continue;
                }

                var containerValue = BitConverter.ToUInt32(bytes, offset + InventoryItemContainerOffset);
                var itemContainer = (ClientInventoryType)containerValue;
                if (!Enum.IsDefined(typeof(ClientInventoryType), itemContainer))
                {
                    itemContainer = container.Type;
                }

                var slot = BitConverter.ToInt16(bytes, offset + InventoryItemSlotOffset);
                if (slot < 0)
                {
                    slot = (short)index;
                }

                yield return new InventoryItemSnapshot
                {
                    Address = container.ItemsAddress + (ulong)(index * InventoryItemSize),
                    Container = itemContainer,
                    Slot = slot,
                    ItemId = itemId,
                    Quantity = quantity,
                    SpiritbondOrCollectability = BitConverter.ToUInt16(bytes, offset + InventoryItemSpiritbondOffset),
                    Condition = BitConverter.ToUInt16(bytes, offset + InventoryItemConditionOffset),
                    Flags = (ClientItemFlags)bytes[offset + InventoryItemFlagsOffset],
                    GlamourId = BitConverter.ToUInt32(bytes, offset + InventoryItemGlamourIdOffset)
                };
            }
        }

        private static IEnumerable<int> FindPattern(byte[] bytes, byte?[] pattern)
        {
            for (var i = 0; i <= bytes.Length - pattern.Length; i++)
            {
                var matched = true;

                for (var j = 0; j < pattern.Length; j++)
                {
                    if (pattern[j].HasValue && bytes[i + j] != pattern[j].Value)
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    yield return i;
                }
            }
        }

        private static bool IsLikelyPointer(ulong address)
        {
            return address >= 0x10000 && address < 0x0000800000000000;
        }

        private sealed class InventoryContainerSnapshot
        {
            public ulong Address { get; set; }

            public ulong ItemsAddress { get; set; }

            public ClientInventoryType Type { get; set; }

            public int Size { get; set; }
        }

        private sealed class ProcessMemory : IDisposable
        {
            private readonly IntPtr handle;

            private ProcessMemory(IntPtr handle)
            {
                this.handle = handle;
            }

            public static ProcessMemory Open(int processId)
            {
                var handle = OpenProcess(ProcessVmRead | ProcessQueryLimitedInformation, false, processId);
                if (handle == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开游戏进程进行内存读取。");
                }

                return new ProcessMemory(handle);
            }

            public ulong ReadUInt64(ulong address)
            {
                return BitConverter.ToUInt64(ReadBytes(address, sizeof(ulong)), 0);
            }

            public byte[] ReadBytes(ulong address, int length)
            {
                byte[] bytes;
                if (!TryReadBytes(address, length, out bytes))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), string.Format("无法读取地址0x{1:X}处的{0}字节。", length, address));
                }

                return bytes;
            }

            public bool TryReadBytes(ulong address, int length, out byte[] bytes)
            {
                bytes = new byte[length];

                if (!IsLikelyPointer(address) || length <= 0)
                {
                    return false;
                }

                IntPtr bytesRead;
                var ok = ReadProcessMemory(handle, new IntPtr(unchecked((long)address)), bytes, length, out bytesRead);
                return ok && bytesRead.ToInt64() == length;
            }

            public void Dispose()
            {
                if (handle != IntPtr.Zero)
                {
                    CloseHandle(handle);
                }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, [Out] byte[] buffer, int size, out IntPtr bytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
