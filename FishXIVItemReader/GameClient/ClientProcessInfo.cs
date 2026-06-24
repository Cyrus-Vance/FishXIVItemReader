namespace FishXIVItemReader.GameClient
{
    public sealed class ClientProcessInfo
    {
        public ClientProcessInfo(int processId, string processName, string modulePath)
        {
            ProcessId = processId;
            ProcessName = processName;
            ModulePath = modulePath;
        }

        public int ProcessId { get; }

        public string ProcessName { get; }

        public string ModulePath { get; }

        public override string ToString()
        {
            return string.Format("{0} ({1})", ProcessName, ProcessId);
        }
    }
}
