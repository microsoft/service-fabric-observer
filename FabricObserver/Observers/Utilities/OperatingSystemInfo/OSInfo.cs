namespace FabricObserver.Observers.Utilities
{
    internal struct OSInfo
    {
        public string Name;
        public string Version;
        public string Status;
        public string Language;
        public int NumberOfProcesses;

        public ulong FreePhysicalMemoryKB;
        public ulong FreeVirtualMemoryKB;
        public ulong TotalVirtualMemorySizeKB;
        public ulong TotalVisibleMemorySizeKB;
        public string InstallDate;
        public string LastBootUpTime;
    }
}
