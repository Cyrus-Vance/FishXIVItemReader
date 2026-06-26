using System;

namespace FishXIVItemReader.GameClient
{
    public sealed class PlayerIdentityInfo
    {
        public PlayerIdentityInfo(
            int processId,
            string processName,
            ulong playerStateAddress,
            bool isLoaded,
            string characterName,
            ulong contentId)
        {
            ReadAt = DateTime.Now;
            ProcessId = processId;
            ProcessName = processName;
            PlayerStateAddress = playerStateAddress;
            IsLoaded = isLoaded;
            CharacterName = characterName ?? string.Empty;
            ContentId = contentId;
        }

        public DateTime ReadAt { get; }

        public int ProcessId { get; }

        public string ProcessName { get; }

        public ulong PlayerStateAddress { get; }

        public bool IsLoaded { get; }

        public string CharacterName { get; }

        public ulong ContentId { get; }
    }
}
