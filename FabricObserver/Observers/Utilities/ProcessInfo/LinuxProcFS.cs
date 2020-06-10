using System.IO;

namespace FabricObserver.Observers.Utilities
{
    internal struct ParsedStatus
    {
        internal int Pid;
        internal ulong VmHWM;
        internal ulong VmRSS;
        internal ulong RssAnon;
        internal ulong RssFile;
        internal ulong RssShmem;
        internal ulong VmData;
        internal ulong VmSwap;
        internal ulong VmSize;
        internal ulong VmPeak;
    }

    internal static class LinuxProcFS
    {
        internal const string RootPath = "/proc/";
        private const string StatusFileName = "/status";

        internal static bool TryParseStatusFile(int pid, out ParsedStatus result)
        {
            string statusFilePath = RootPath + pid.ToString() + StatusFileName;

            return TryParseStatusFile(statusFilePath, out result);
        }

        internal static bool TryParseStatusFile(string statusFilePath, out ParsedStatus result)
        {
            result = default(ParsedStatus);

            if (!TryReadFile(statusFilePath, out string[] fileLines))
            {
                return false;
            }

            for (int i = 0; i < fileLines.Length; ++i)
            {
                string line = fileLines[i];

                int idx = line.IndexOf(':');

                string title = line.Substring(0, idx);

                switch (title)
                {
                    case nameof(ParsedStatus.Pid):
                        result.Pid = (int)ReadUInt64(line, idx + 2);
                        break;
                    case nameof(ParsedStatus.VmHWM):
                        result.VmHWM = ReadUInt64(line, idx + 2) * 1024;
                        break;
                    case nameof(ParsedStatus.VmRSS):
                        result.VmRSS = ReadUInt64(line, idx + 2) * 1024;
                        break;
                    case nameof(ParsedStatus.RssAnon):
                        result.RssAnon = ReadUInt64(line, idx + 2) * 1024;
                        break;
                    case nameof(ParsedStatus.RssFile):
                        result.RssFile = ReadUInt64(line, idx + 2) * 1024;
                        break;
                    case nameof(ParsedStatus.RssShmem):
                        result.RssShmem = ReadUInt64(line, idx + 2) * 1024;
                        break;
                    case nameof(ParsedStatus.VmData):
                        result.VmData = ReadUInt64(line, idx + 2) * 1024;
                        break;
                    case nameof(ParsedStatus.VmSwap):
                        result.VmSwap = ReadUInt64(line, idx + 2) * 1024;
                        break;
                    case nameof(ParsedStatus.VmSize):
                        result.VmSize = ReadUInt64(line, idx + 2) * 1024;
                        break;
                    case nameof(ParsedStatus.VmPeak):
                        result.VmPeak = ReadUInt64(line, idx + 2) * 1024;
                        break;
                }
            }

            return true;
        }

        private static bool TryReadFile(string filePath, out string[] fileLines)
        {
            try
            {
                fileLines = File.ReadAllLines(filePath);
                return true;
            }
            catch (IOException)
            {
                fileLines = null;
                return false;
            }
        }

        private static ulong ReadUInt64(string line, int startIndex)
        {
            ulong result = 0;

            while (line[startIndex] == ' ')
            {
                ++startIndex;
            }

            int len = line.Length;

            while (startIndex < len)
            {
                char c = line[startIndex];

                int d = c - '0';

                if (d >= 0 && d <= 9)
                {
                    result = checked((result * 10ul) + (ulong)d);
                    ++startIndex;
                }
                else
                {
                    break;
                }
            }

            return result;
        }
    }
}