using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace FishXIVItemReader.GameClient
{
    public sealed class DeucalionInventoryReader : IDisposable
    {
        private const string CaptureName = "FishXIVItemReader";
        private const int DeucalionOperationDebug = 0;
        private const int DeucalionOperationRecv = 3;
        private const int DeucalionOperationSend = 4;
        private const int DeucalionOperationOption = 5;
        private const int ZoneChannel = 1;
        private const int IpcHeaderSize = 0x20;
        private const int MaxDeucalionPayloadSize = 4 * 1024 * 1024;

        private static readonly uint[] PlayerInventoryTypes =
        {
            (uint)ClientInventoryType.Inventory1,
            (uint)ClientInventoryType.Inventory2,
            (uint)ClientInventoryType.Inventory3,
            (uint)ClientInventoryType.Inventory4
        };

        private static readonly uint[] EquippedInventoryTypes =
        {
            (uint)ClientInventoryType.EquippedItems
        };

        private static readonly uint[] SaddleBagInventoryTypes =
        {
            (uint)ClientInventoryType.SaddleBag1,
            (uint)ClientInventoryType.SaddleBag2,
            (uint)ClientInventoryType.PremiumSaddleBag1,
            (uint)ClientInventoryType.PremiumSaddleBag2
        };

        private static readonly uint[] ArmoryChestInventoryTypes =
        {
            (uint)ClientInventoryType.ArmoryMainHand,
            (uint)ClientInventoryType.ArmoryOffHand,
            (uint)ClientInventoryType.ArmoryHead,
            (uint)ClientInventoryType.ArmoryBody,
            (uint)ClientInventoryType.ArmoryHands,
            (uint)ClientInventoryType.ArmoryWaist,
            (uint)ClientInventoryType.ArmoryLegs,
            (uint)ClientInventoryType.ArmoryFeets,
            (uint)ClientInventoryType.ArmoryEar,
            (uint)ClientInventoryType.ArmoryNeck,
            (uint)ClientInventoryType.ArmoryWrist,
            (uint)ClientInventoryType.ArmoryRings,
            (uint)ClientInventoryType.ArmorySoulCrystal
        };

        private static readonly uint[] RetainerStorageInventoryTypes =
        {
            (uint)ClientInventoryType.RetainerPage1,
            (uint)ClientInventoryType.RetainerPage2,
            (uint)ClientInventoryType.RetainerPage3,
            (uint)ClientInventoryType.RetainerPage4,
            (uint)ClientInventoryType.RetainerPage5,
            (uint)ClientInventoryType.RetainerPage6,
            (uint)ClientInventoryType.RetainerPage7
        };

        private readonly object gate = new object();
        private readonly ManualResetEventSlim snapshotChanged = new ManualResetEventSlim(false);
        private readonly Dictionary<InventorySlotKey, InventoryItemSnapshot> items =
            new Dictionary<InventorySlotKey, InventoryItemSnapshot>();
        private readonly List<QueuedItemInfo> queuedItemInfos = new List<QueuedItemInfo>();

        private NamedPipeClientStream pipe;
        private Thread readThread;
        private bool stopRequested;
        private int processId;
        private string processName;
        private OpcodeRegistry opcodeRegistry;
        private string lastError;
        private int packetsSeen;
        private int zonePacketsSeen;
        private int recognizedPacketsSeen;
        private int inventoryPacketsSeen;
        private int unknownOpcodePacketsSeen;
        private int lastOpcode;
        private string lastPacketDirection;
        private string lastPacketType;
        private DateTime lastZonePacketAt;
        private DateTime lastRecognizedPacketAt;
        private DateTime lastInventoryPacketAt;

        public InventoryReadResult ReadPlayerInventory(
            int processId,
            bool includeSaddleBags,
            bool includeRetainerStorage,
            bool includeArmoryChest,
            bool includeEquippedItems,
            TimeSpan waitForSnapshot)
        {
            var requestedTypes = CreateRequestedInventoryTypes(
                includeSaddleBags,
                includeRetainerStorage,
                includeArmoryChest,
                includeEquippedItems);

            EnsureStarted(processId);

            if (!HasAnyRequestedItem(requestedTypes))
            {
                snapshotChanged.Reset();
                snapshotChanged.Wait(waitForSnapshot);
            }

            lock (gate)
            {
                var matchingItems = items
                    .Where(pair => requestedTypes.Contains(pair.Key.ContainerId))
                    .Select(pair => CloneSnapshot(pair.Value))
                    .OrderBy(item => (uint)item.Container)
                    .ThenBy(item => item.Slot)
                    .ToList();

                if (matchingItems.Count == 0)
                {
                    if (!string.IsNullOrEmpty(lastError))
                    {
                        throw new InvalidOperationException(lastError);
                    }

                    return CreateResult(matchingItems);
                }

                return CreateResult(matchingItems);
            }
        }

        public DeucalionCaptureStatus GetCaptureStatus()
        {
            lock (gate)
            {
                return new DeucalionCaptureStatus(
                    pipe != null && pipe.IsConnected,
                    processId,
                    zonePacketsSeen,
                    recognizedPacketsSeen,
                    inventoryPacketsSeen,
                    packetsSeen,
                    unknownOpcodePacketsSeen,
                    lastOpcode,
                    lastPacketDirection,
                    lastPacketType,
                    lastZonePacketAt,
                    lastRecognizedPacketAt,
                    lastInventoryPacketAt,
                    items.Count,
                    queuedItemInfos.Count,
                    lastError);
            }
        }

        public void Stop()
        {
            Thread threadToJoin = null;

            lock (gate)
            {
                stopRequested = true;
                if (pipe != null)
                {
                    pipe.Dispose();
                    pipe = null;
                }

                threadToJoin = readThread;
                readThread = null;
            }

            if (threadToJoin != null && threadToJoin != Thread.CurrentThread && threadToJoin.IsAlive)
            {
                threadToJoin.Join(1000);
            }
        }

        public void Dispose()
        {
            Stop();
            snapshotChanged.Dispose();
        }

        private void EnsureStarted(int targetProcessId)
        {
            lock (gate)
            {
                if (pipe != null && pipe.IsConnected && readThread != null && readThread.IsAlive && processId == targetProcessId)
                {
                    return;
                }
            }

            Stop();

            var registry = OpcodeRegistry.LoadDefault();
            var targetProcessName = GetProcessName(targetProcessId);
            var pipeName = "deucalion-" + targetProcessId;
            var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                client.Connect(1500);
            }
            catch (TimeoutException ex)
            {
                client.Dispose();
                throw new InvalidOperationException(
                    string.Format(
                        "没有找到Deucalion管道 \\\\.\\pipe\\{0}。请在FFXIV_ACT_PLugin中启用Deucalion网络模式，并启动游戏。",
                        pipeName),
                    ex);
            }
            catch (IOException ex)
            {
                client.Dispose();
                throw new InvalidOperationException(
                    string.Format("无法连接Deucalion管道 \\\\.\\pipe\\{0}：{1}", pipeName, ex.Message),
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                client.Dispose();
                throw new InvalidOperationException(
                    string.Format(
                        "没有权限连接Deucalion 管道 \\\\.\\pipe\\{0}。",
                        pipeName),
                    ex);
            }
            catch (Win32Exception ex)
            {
                client.Dispose();
                throw new InvalidOperationException(
                    string.Format("无法连接 Deucalion 管道 \\\\.\\pipe\\{0}：{1}", pipeName, ex.Message),
                    ex);
            }

            SendHandshake(client);

            lock (gate)
            {
                items.Clear();
                queuedItemInfos.Clear();
                snapshotChanged.Reset();
                stopRequested = false;
                processId = targetProcessId;
                processName = targetProcessName;
                opcodeRegistry = registry;
                pipe = client;
                lastError = null;
                packetsSeen = 0;
                zonePacketsSeen = 0;
                recognizedPacketsSeen = 0;
                inventoryPacketsSeen = 0;
                unknownOpcodePacketsSeen = 0;
                lastOpcode = 0;
                lastPacketDirection = null;
                lastPacketType = null;
                lastZonePacketAt = DateTime.MinValue;
                lastRecognizedPacketAt = DateTime.MinValue;
                lastInventoryPacketAt = DateTime.MinValue;

                readThread = new Thread(ReadLoop)
                {
                    IsBackground = true,
                    Name = "FishXIVItemReader Deucalion"
                };
                readThread.Start();
            }
        }

        private static HashSet<uint> CreateRequestedInventoryTypes(
            bool includeSaddleBags,
            bool includeRetainerStorage,
            bool includeArmoryChest,
            bool includeEquippedItems)
        {
            var result = new HashSet<uint>(PlayerInventoryTypes);

            if (includeEquippedItems)
            {
                foreach (var type in EquippedInventoryTypes)
                    result.Add(type);
            }

            if (includeSaddleBags)
            {
                foreach (var type in SaddleBagInventoryTypes)
                    result.Add(type);
            }

            if (includeRetainerStorage)
            {
                foreach (var type in RetainerStorageInventoryTypes)
                    result.Add(type);
            }

            if (includeArmoryChest)
            {
                foreach (var type in ArmoryChestInventoryTypes)
                    result.Add(type);
            }

            return result;
        }

        private bool HasAnyRequestedItem(HashSet<uint> requestedTypes)
        {
            lock (gate)
            {
                return items.Keys.Any(key => requestedTypes.Contains(key.ContainerId));
            }
        }

        private static InventoryItemSnapshot CloneSnapshot(InventoryItemSnapshot item)
        {
            return new InventoryItemSnapshot
            {
                Address = item.Address,
                Container = item.Container,
                Slot = item.Slot,
                ItemId = item.ItemId,
                Quantity = item.Quantity,
                SpiritbondOrCollectability = item.SpiritbondOrCollectability,
                Condition = item.Condition,
                Flags = item.Flags,
                GlamourId = item.GlamourId
            };
        }

        private static string GetProcessName(int targetProcessId)
        {
            try
            {
                using (var process = Process.GetProcessById(targetProcessId))
                {
                    return process.ProcessName;
                }
            }
            catch
            {
                return "ffxiv_dx11";
            }
        }

        private void ReadLoop()
        {
            var pending = new List<byte>(8192);
            var buffer = new byte[8192];

            try
            {
                while (!IsStopRequested())
                {
                    NamedPipeClientStream currentPipe;
                    lock (gate)
                    {
                        currentPipe = pipe;
                    }

                    if (currentPipe == null)
                    {
                        return;
                    }

                    var read = currentPipe.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        SetLastError("Deucalion 管道已关闭。");
                        return;
                    }

                    for (var i = 0; i < read; i++)
                    {
                        pending.Add(buffer[i]);
                    }

                    ProcessPendingFrames(pending);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException ex)
            {
                if (!IsStopRequested())
                    SetLastError("读取 Deucalion 管道失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                if (!IsStopRequested())
                    SetLastError("处理 Deucalion 数据失败：" + ex.Message);
            }
        }

        private bool IsStopRequested()
        {
            lock (gate)
            {
                return stopRequested;
            }
        }

        private void SetLastError(string message)
        {
            lock (gate)
            {
                lastError = message;
            }

            snapshotChanged.Set();
        }

        private void ProcessPendingFrames(List<byte> pending)
        {
            while (pending.Count >= 4)
            {
                var frameSize = ReadInt32(pending, 0);
                if (frameSize < 9 || frameSize > MaxDeucalionPayloadSize)
                {
                    pending.Clear();
                    throw new InvalidDataException("Deucalion payload 长度无效：" + frameSize);
                }

                if (pending.Count < frameSize)
                {
                    return;
                }

                var frame = pending.GetRange(0, frameSize).ToArray();
                pending.RemoveRange(0, frameSize);
                ProcessDeucalionFrame(frame);
            }
        }

        private void ProcessDeucalionFrame(byte[] frame)
        {
            var operation = frame[4];
            var channel = BitConverter.ToInt32(frame, 5);
            if ((operation != DeucalionOperationRecv && operation != DeucalionOperationSend) || channel != ZoneChannel)
            {
                return;
            }

            var xivDataLength = frame.Length - 9;
            if (xivDataLength <= IpcHeaderSize)
            {
                return;
            }

            var xivData = new byte[xivDataLength];
            Buffer.BlockCopy(frame, 9, xivData, 0, xivDataLength);

            var opcode = BitConverter.ToInt16(xivData, 0x12);
            var serverOrigin = operation == DeucalionOperationRecv;
            string typeName;
            lock (gate)
            {
                zonePacketsSeen++;
                lastOpcode = opcode;
                lastPacketDirection = serverOrigin ? "S" : "C";
                lastZonePacketAt = DateTime.Now;

                typeName = opcodeRegistry != null
                    ? opcodeRegistry.Resolve(serverOrigin, opcode)
                    : null;

                if (string.IsNullOrEmpty(typeName))
                {
                    unknownOpcodePacketsSeen++;
                }
                else
                {
                    recognizedPacketsSeen++;
                    lastPacketType = typeName;
                    lastRecognizedPacketAt = lastZonePacketAt;
                }
            }

            if (string.IsNullOrEmpty(typeName))
            {
                return;
            }

            var dataLength = xivData.Length - IpcHeaderSize;
            var data = new byte[dataLength];
            Buffer.BlockCopy(xivData, IpcHeaderSize, data, 0, dataLength);

            ProcessIpcPacket(NormalizeTypeName(typeName), data);
        }

        private static string NormalizeTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return string.Empty;

            return char.ToLowerInvariant(typeName[0]) + typeName.Substring(1);
        }

        private void ProcessIpcPacket(string typeName, byte[] data)
        {
            var inventoryPacket = false;
            var changed = false;

            switch (typeName)
            {
                case "itemInfo":
                    inventoryPacket = true;
                    changed = QueueItemInfo(data, false);
                    break;
                case "currencyCrystalInfo":
                    inventoryPacket = true;
                    changed = QueueItemInfo(data, true);
                    break;
                case "containerInfo":
                    inventoryPacket = true;
                    changed = ProcessContainerInfo(data);
                    break;
                case "updateInventorySlot":
                    inventoryPacket = true;
                    changed = ProcessUpdateInventorySlot(data);
                    break;
                case "inventoryTransaction":
                    inventoryPacket = true;
                    changed = ProcessInventoryTransaction(data);
                    break;
                case "inventoryModifyHandler":
                    inventoryPacket = true;
                    changed = ProcessInventoryModifyHandler(data);
                    break;
            }

            if (inventoryPacket)
            {
                lock (gate)
                {
                    inventoryPacketsSeen++;
                    lastInventoryPacketAt = DateTime.Now;
                }
            }

            if (changed)
            {
                lock (gate)
                {
                    packetsSeen++;
                }

                snapshotChanged.Set();
            }
        }

        private InventoryReadResult CreateResult(IReadOnlyList<InventoryItemSnapshot> matchingItems)
        {
            return new InventoryReadResult(
                processId,
                processName,
                InventoryReadMode.Network,
                string.Empty,
                0,
                0,
                matchingItems.Select(item => (uint)item.Container).Distinct().Count(),
                matchingItems.Count,
                matchingItems);
        }

        private bool QueueItemInfo(byte[] data, bool currencyCrystal)
        {
            var reader = new PacketReader(data);
            QueuedItemInfo item;

            try
            {
                if (currencyCrystal)
                {
                    item = new QueuedItemInfo
                    {
                        Sequence = reader.ReadUInt32(),
                        ContainerId = reader.ReadUInt16(),
                        Slot = reader.ReadUInt16(),
                        Quantity = reader.ReadUInt32()
                    };
                    reader.ReadUInt32();
                    item.CatalogId = reader.ReadUInt32();
                }
                else
                {
                    item = ReadItemLikePacket(reader, true);
                }
            }
            catch (EndOfStreamException)
            {
                return false;
            }

            if (item.CatalogId == 0 || item.Quantity == 0)
            {
                return false;
            }

            lock (gate)
            {
                queuedItemInfos.Add(item);
                if (queuedItemInfos.Count > 4096)
                {
                    queuedItemInfos.RemoveRange(0, queuedItemInfos.Count - 4096);
                }
            }

            return false;
        }

        private bool ProcessContainerInfo(byte[] data)
        {
            ContainerInfoPacket packet;
            try
            {
                var reader = new PacketReader(data);
                packet = new ContainerInfoPacket
                {
                    Sequence = reader.ReadUInt32(),
                    NumItems = reader.ReadUInt32(),
                    ContainerId = reader.ReadUInt32()
                };
            }
            catch (EndOfStreamException)
            {
                return false;
            }

            lock (gate)
            {
                var matching = queuedItemInfos
                    .Where(item => item.Sequence == packet.Sequence)
                    .ToList();
                queuedItemInfos.RemoveAll(item => item.Sequence == packet.Sequence);

                if (matching.Count == 0)
                {
                    return false;
                }

                RemoveContainer(packet.ContainerId);
                foreach (var item in matching)
                {
                    AddOrUpdateItem(item.ContainerId, item.Slot, item);
                }
            }

            return true;
        }

        private bool ProcessUpdateInventorySlot(byte[] data)
        {
            QueuedItemInfo item;
            try
            {
                item = ReadItemLikePacket(new PacketReader(data), false);
            }
            catch (EndOfStreamException)
            {
                return false;
            }

            lock (gate)
            {
                ApplySlotUpdate(item.ContainerId, item.Slot, item);
            }

            return true;
        }

        private bool ProcessInventoryTransaction(byte[] data)
        {
            try
            {
                var reader = new PacketReader(data);
                reader.ReadUInt32();
                var type = reader.ReadUInt16();
                reader.ReadUInt16();
                reader.ReadUInt32();
                var containerId = reader.ReadUInt32();
                var slot = reader.ReadUInt16();
                reader.ReadUInt16();
                var quantity = reader.ReadUInt32();
                var catalogId = reader.ReadUInt32();

                if (opcodeRegistry != null && type != opcodeRegistry.InventoryOperationBaseValue)
                {
                    return false;
                }

                var item = new QueuedItemInfo
                {
                    ContainerId = containerId,
                    Slot = slot,
                    Quantity = quantity,
                    CatalogId = catalogId
                };

                lock (gate)
                {
                    ApplySlotUpdate(containerId, slot, item);
                }
            }
            catch (EndOfStreamException)
            {
                return false;
            }

            return true;
        }

        private bool ProcessInventoryModifyHandler(byte[] data)
        {
            InventoryModifyPacket packet;

            try
            {
                var reader = new PacketReader(data);
                packet = new InventoryModifyPacket
                {
                    Sequence = reader.ReadUInt32(),
                    ActionValue = reader.ReadUInt16()
                };
                reader.Skip(6);
                packet.FromContainer = reader.ReadUInt16();
                reader.Skip(2);
                packet.FromSlot = reader.ReadByte();
                reader.Skip(15);
                packet.ToContainer = reader.ReadUInt16();
                reader.Skip(2);
                packet.ToSlot = reader.ReadByte();
                reader.Skip(3);
                packet.SplitCount = reader.ReadUInt32();
            }
            catch (EndOfStreamException)
            {
                return false;
            }

            lock (gate)
            {
                return ApplyInventoryModify(packet);
            }
        }

        private static QueuedItemInfo ReadItemLikePacket(PacketReader reader, bool hasContainerSequence)
        {
            var item = new QueuedItemInfo();
            if (hasContainerSequence)
            {
                item.Sequence = reader.ReadUInt32();
                reader.ReadUInt32();
            }
            else
            {
                item.Sequence = reader.ReadUInt32();
                reader.ReadUInt32();
            }

            item.ContainerId = reader.ReadUInt16();
            item.Slot = reader.ReadUInt16();
            item.Quantity = reader.ReadUInt32();
            item.CatalogId = reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt64();
            item.HighQuality = reader.ReadByte() != 0;
            reader.ReadByte();
            item.Condition = reader.ReadUInt16();
            item.SpiritBond = reader.ReadUInt16();
            reader.ReadUInt16();
            item.GlamourCatalogId = reader.ReadUInt32();
            reader.Skip(10);
            reader.Skip(5);
            reader.Skip(1);
            reader.ReadUInt32();
            return item;
        }

        private void ApplySlotUpdate(uint containerId, int slot, QueuedItemInfo item)
        {
            if (item.CatalogId == 0 || item.Quantity == 0)
            {
                items.Remove(new InventorySlotKey(containerId, slot));
                return;
            }

            AddOrUpdateItem(containerId, slot, item);
        }

        private bool ApplyInventoryModify(InventoryModifyPacket packet)
        {
            var action = packet.ActionValue - (opcodeRegistry != null ? opcodeRegistry.InventoryOperationBaseValue : 0);
            var fromKey = new InventorySlotKey(packet.FromContainer, packet.FromSlot);
            var toKey = new InventorySlotKey(packet.ToContainer, packet.ToSlot);

            InventoryItemSnapshot fromItem;
            items.TryGetValue(fromKey, out fromItem);
            InventoryItemSnapshot toItem;
            items.TryGetValue(toKey, out toItem);

            switch (action)
            {
                case 0:
                    if (fromItem == null)
                        return false;
                    if (packet.SplitCount == 0 || packet.SplitCount >= fromItem.Quantity)
                    {
                        items.Remove(fromKey);
                    }
                    else
                    {
                        fromItem.Quantity -= (int)packet.SplitCount;
                    }

                    return true;

                case 1:
                case 15:
                case 16:
                    if (fromItem == null)
                        return false;
                    items.Remove(fromKey);
                    fromItem.Container = (ClientInventoryType)packet.ToContainer;
                    fromItem.Slot = packet.ToSlot;
                    items[toKey] = fromItem;
                    return true;

                case 2:
                    if (fromItem == null || toItem == null)
                        return false;
                    items[fromKey] = toItem;
                    items[toKey] = fromItem;
                    toItem.Container = (ClientInventoryType)packet.FromContainer;
                    toItem.Slot = packet.FromSlot;
                    fromItem.Container = (ClientInventoryType)packet.ToContainer;
                    fromItem.Slot = packet.ToSlot;
                    return true;

                case 3:
                case 4:
                case 6:
                case 10:
                    if (fromItem == null || packet.SplitCount == 0)
                        return false;
                    fromItem.Quantity -= (int)packet.SplitCount;
                    if (fromItem.Quantity <= 0)
                    {
                        items.Remove(fromKey);
                    }

                    if (toItem == null)
                    {
                        items[toKey] = new InventoryItemSnapshot
                        {
                            Container = (ClientInventoryType)packet.ToContainer,
                            Slot = packet.ToSlot,
                            ItemId = fromItem.ItemId,
                            Quantity = (int)packet.SplitCount,
                            Flags = fromItem.Flags,
                            Condition = fromItem.Condition,
                            SpiritbondOrCollectability = fromItem.SpiritbondOrCollectability,
                            GlamourId = fromItem.GlamourId
                        };
                    }
                    else
                    {
                        toItem.Quantity += (int)packet.SplitCount;
                    }

                    return true;

                case 5:
                    if (fromItem == null || toItem == null)
                        return false;
                    items.Remove(fromKey);
                    toItem.Quantity += fromItem.Quantity;
                    return true;
            }

            return false;
        }

        private void RemoveContainer(uint containerId)
        {
            var keys = items.Keys
                .Where(key => key.ContainerId == containerId)
                .ToList();

            foreach (var key in keys)
            {
                items.Remove(key);
            }
        }

        private void AddOrUpdateItem(uint containerId, int slot, QueuedItemInfo item)
        {
            var flags = item.HighQuality ? ClientItemFlags.HighQuality : ClientItemFlags.None;
            items[new InventorySlotKey(containerId, slot)] = new InventoryItemSnapshot
            {
                Address = 0,
                Container = (ClientInventoryType)containerId,
                Slot = slot,
                ItemId = item.CatalogId,
                Quantity = (int)item.Quantity,
                SpiritbondOrCollectability = item.SpiritBond,
                Condition = item.Condition,
                Flags = flags,
                GlamourId = item.GlamourCatalogId
            };
        }

        private static void SendHandshake(Stream stream)
        {
            var optionPayload = new byte[9];
            WriteInt32(optionPayload, 0, 9);
            optionPayload[4] = DeucalionOperationOption;
            WriteInt32(optionPayload, 5, (1 << 1) | (1 << 4));
            stream.Write(optionPayload, 0, optionPayload.Length);

            var nameBytes = Encoding.UTF8.GetBytes(CaptureName);
            var nicknamePayload = new byte[9 + nameBytes.Length];
            WriteInt32(nicknamePayload, 0, nicknamePayload.Length);
            nicknamePayload[4] = DeucalionOperationDebug;
            WriteInt32(nicknamePayload, 5, 9000);
            Buffer.BlockCopy(nameBytes, 0, nicknamePayload, 9, nameBytes.Length);
            stream.Write(nicknamePayload, 0, nicknamePayload.Length);
            stream.Flush();
        }

        private static int ReadInt32(List<byte> bytes, int offset)
        {
            return bytes[offset] |
                (bytes[offset + 1] << 8) |
                (bytes[offset + 2] << 16) |
                (bytes[offset + 3] << 24);
        }

        private static void WriteInt32(byte[] bytes, int offset, int value)
        {
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
        }

        private struct InventorySlotKey : IEquatable<InventorySlotKey>
        {
            public InventorySlotKey(uint containerId, int slot)
            {
                ContainerId = containerId;
                Slot = slot;
            }

            public uint ContainerId { get; }

            public int Slot { get; }

            public bool Equals(InventorySlotKey other)
            {
                return ContainerId == other.ContainerId && Slot == other.Slot;
            }

            public override bool Equals(object obj)
            {
                return obj is InventorySlotKey && Equals((InventorySlotKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((int)ContainerId * 397) ^ Slot;
                }
            }
        }

        private sealed class QueuedItemInfo
        {
            public uint Sequence { get; set; }

            public uint ContainerId { get; set; }

            public ushort Slot { get; set; }

            public uint Quantity { get; set; }

            public uint CatalogId { get; set; }

            public bool HighQuality { get; set; }

            public ushort Condition { get; set; }

            public ushort SpiritBond { get; set; }

            public uint GlamourCatalogId { get; set; }
        }

        private sealed class ContainerInfoPacket
        {
            public uint Sequence { get; set; }

            public uint NumItems { get; set; }

            public uint ContainerId { get; set; }
        }

        private sealed class InventoryModifyPacket
        {
            public uint Sequence { get; set; }

            public ushort ActionValue { get; set; }

            public ushort FromContainer { get; set; }

            public byte FromSlot { get; set; }

            public ushort ToContainer { get; set; }

            public byte ToSlot { get; set; }

            public uint SplitCount { get; set; }
        }

        private sealed class PacketReader
        {
            private readonly byte[] bytes;
            private int offset;

            public PacketReader(byte[] bytes)
            {
                this.bytes = bytes;
            }

            public byte ReadByte()
            {
                Require(1);
                return bytes[offset++];
            }

            public ushort ReadUInt16()
            {
                Require(2);
                var value = BitConverter.ToUInt16(bytes, offset);
                offset += 2;
                return value;
            }

            public uint ReadUInt32()
            {
                Require(4);
                var value = BitConverter.ToUInt32(bytes, offset);
                offset += 4;
                return value;
            }

            public ulong ReadUInt64()
            {
                Require(8);
                var value = BitConverter.ToUInt64(bytes, offset);
                offset += 8;
                return value;
            }

            public void Skip(int count)
            {
                Require(count);
                offset += count;
            }

            private void Require(int count)
            {
                if (count < 0 || offset + count > bytes.Length)
                {
                    throw new EndOfStreamException();
                }
            }
        }

        private sealed class OpcodeRegistry
        {
            private const string ResourceName = "FishXIVItemReader.GameClient.DeucalionOpcodes.json";
            private const string OpcodeConfigurationErrorMessage = "Opcodes配置异常，请联系作者";

            private readonly Dictionary<int, string> serverOpcodes;
            private readonly Dictionary<int, string> clientOpcodes;

            private OpcodeRegistry(
                Dictionary<int, string> serverOpcodes,
                Dictionary<int, string> clientOpcodes,
                int inventoryOperationBaseValue)
            {
                this.serverOpcodes = serverOpcodes;
                this.clientOpcodes = clientOpcodes;
                InventoryOperationBaseValue = inventoryOperationBaseValue;
            }

            public int InventoryOperationBaseValue { get; }

            public static OpcodeRegistry LoadDefault()
            {
                try
                {
                    return LoadDefaultCore();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(OpcodeConfigurationErrorMessage, ex);
                }
            }

            private static OpcodeRegistry LoadDefaultCore()
            {
                var config = Deserialize<OpcodeConfig>(ReadEmbeddedResource(ResourceName));
                if (config == null)
                {
                    throw new InvalidOperationException();
                }

                var serverOpcodes = new Dictionary<int, string>();
                var clientOpcodes = new Dictionary<int, string>();
                foreach (var entry in config.opcodes ?? new List<OpcodeEntry>())
                {
                    if (entry.serverOpcode > 0)
                    {
                        serverOpcodes[entry.serverOpcode] = entry.name;
                    }

                    if (entry.clientOpcode > 0)
                    {
                        clientOpcodes[entry.clientOpcode] = entry.name;
                    }
                }

                if (serverOpcodes.Count == 0 && clientOpcodes.Count == 0)
                {
                    throw new InvalidOperationException();
                }

                return new OpcodeRegistry(
                    serverOpcodes,
                    clientOpcodes,
                    config.inventoryOperationBaseValue);
            }

            public string Resolve(bool serverOrigin, int opcode)
            {
                string name;
                return (serverOrigin ? serverOpcodes : clientOpcodes).TryGetValue(opcode, out name)
                    ? name
                    : null;
            }

            private static T Deserialize<T>(string json)
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return (T)serializer.ReadObject(stream);
                }
            }

            private static string ReadEmbeddedResource(string resourceName)
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        throw new InvalidOperationException();
                    }

                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }

            [DataContract]
            private sealed class OpcodeConfig
            {
                [DataMember(Name = "schemaVersion")]
                public int schemaVersion { get; set; }

                [DataMember(Name = "inventoryOperationBaseValue")]
                public int inventoryOperationBaseValue { get; set; }

                [DataMember(Name = "opcodes")]
                public List<OpcodeEntry> opcodes { get; set; }
            }

            [DataContract]
            private sealed class OpcodeEntry
            {
                [DataMember(Name = "name")]
                public string name { get; set; }

                [DataMember(Name = "serverOpcode")]
                public int serverOpcode { get; set; }

                [DataMember(Name = "clientOpcode")]
                public int clientOpcode { get; set; }
            }
        }
    }
}
