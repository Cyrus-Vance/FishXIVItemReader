using System;

namespace FishXIVItemReader.GameClient
{
    public sealed class DeucalionCaptureStatus
    {
        public DeucalionCaptureStatus(
            bool connected,
            int processId,
            int zonePacketsSeen,
            int recognizedPacketsSeen,
            int inventoryPacketsSeen,
            int inventorySnapshotUpdates,
            int unknownOpcodePacketsSeen,
            int lastOpcode,
            string lastPacketDirection,
            string lastPacketType,
            DateTime lastZonePacketAt,
            DateTime lastRecognizedPacketAt,
            DateTime lastInventoryPacketAt,
            int cachedItems,
            int queuedItemInfos,
            string lastError)
        {
            Connected = connected;
            ProcessId = processId;
            ZonePacketsSeen = zonePacketsSeen;
            RecognizedPacketsSeen = recognizedPacketsSeen;
            InventoryPacketsSeen = inventoryPacketsSeen;
            InventorySnapshotUpdates = inventorySnapshotUpdates;
            UnknownOpcodePacketsSeen = unknownOpcodePacketsSeen;
            LastOpcode = lastOpcode;
            LastPacketDirection = lastPacketDirection;
            LastPacketType = lastPacketType;
            LastZonePacketAt = lastZonePacketAt;
            LastRecognizedPacketAt = lastRecognizedPacketAt;
            LastInventoryPacketAt = lastInventoryPacketAt;
            CachedItems = cachedItems;
            QueuedItemInfos = queuedItemInfos;
            LastError = lastError;
        }

        public bool Connected { get; }

        public int ProcessId { get; }

        public int ZonePacketsSeen { get; }

        public int RecognizedPacketsSeen { get; }

        public int InventoryPacketsSeen { get; }

        public int InventorySnapshotUpdates { get; }

        public int UnknownOpcodePacketsSeen { get; }

        public int LastOpcode { get; }

        public string LastPacketDirection { get; }

        public string LastPacketType { get; }

        public DateTime LastZonePacketAt { get; }

        public DateTime LastRecognizedPacketAt { get; }

        public DateTime LastInventoryPacketAt { get; }

        public int CachedItems { get; }

        public int QueuedItemInfos { get; }

        public string LastError { get; }
    }
}
