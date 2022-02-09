// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.CodeDom;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Linq;

namespace FabricObserver.Observers.Utilities
{
    public class LinuxProcessInfoProvider : ProcessInfoProvider
    {
        private const int MaxDescendants = 50;

        public override float GetProcessWorkingSetMb(int processId, string procName = null, bool getPrivateWorkingSet = false)
        {
            if (LinuxProcFS.TryParseStatusFile(processId, out ParsedStatus status))
            {
                return (status.VmRSS - status.RsSFile) / 1048576f;
            }

            // Could not read from /proc/[pid]/status - it is possible that process already exited.
            return 0f;
        }

        public override float GetProcessAllocatedHandles(int processId, StatelessServiceContext context = null, bool useProcessObject = false)
        {
            if (processId < 0 || context == null)
            {
                return -1f;
            }

            // We need the full path to the currently deployed FO CodePackage, which is where our 
            // proxy binary lives.
            string path = context.CodePackageActivationContext.GetCodePackageObject("Code").Path;
            string arg = processId.ToString();
            string bin = $"{path}/elevated_proc_fd";
            float result;

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                Arguments = arg,
                FileName = bin,
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            using (Process process = Process.Start(startInfo))
            {
                var stdOut = process.StandardOutput;
                string output = stdOut.ReadToEnd();

                process.WaitForExit();

                result = float.TryParse(output, out float ret) ? ret : -42f;

                if (process.ExitCode != 0)
                {
                    Logger.LogWarning($"elevated_proc_fd exited with: {process.ExitCode}");
                    return -1f;
                }
            }

            return result;
        }

        public override List<(string ProcName, int Pid)> GetChildProcessInfo(int parentPid)
        {
            if (parentPid < 1)
            {
                return null;
            }

            // Get child procs.
            List<(string ProcName, int Pid)> childProcesses = TupleGetChildProcessInfo(parentPid);

            if (childProcesses == null || childProcesses.Count == 0)
            {
                return null;
            }

            if (childProcesses.Count >= MaxDescendants)
            {
                return childProcesses.Take(MaxDescendants).ToList();
            }

            // Get descendant proc at max depth = 5 and max number of descendants = 50. 
            for (int i = 0; i < childProcesses.Count; ++i)
            {
                List<(string ProcName, int Pid)> c1 = TupleGetChildProcessInfo(childProcesses[i].Pid);

                if (c1 != null && c1.Count > 0)
                {
                    childProcesses.AddRange(c1);

                    if (childProcesses.Count >= MaxDescendants)
                    {
                        return childProcesses.Take(MaxDescendants).ToList();
                    }

                    for (int j = 0; j < c1.Count; ++j)
                    {
                        List<(string ProcName, int Pid)> c2 = TupleGetChildProcessInfo(c1[j].Pid);

                        if (c2 != null && c2.Count > 0)
                        {
                            childProcesses.AddRange(c2);

                            if (childProcesses.Count >= MaxDescendants)
                            {
                                return childProcesses.Take(MaxDescendants).ToList();
                            }

                            for (int k = 0; k < c2.Count; ++k)
                            {
                                List<(string ProcName, int Pid)> c3 = TupleGetChildProcessInfo(c2[k].Pid);

                                if (c3 != null && c3.Count > 0)
                                {
                                    childProcesses.AddRange(c3);

                                    if (childProcesses.Count >= MaxDescendants)
                                    {
                                        return childProcesses.Take(MaxDescendants).ToList();
                                    }

                                    for (int l = 0; l < c3.Count; ++l)
                                    {
                                        List<(string ProcName, int Pid)> c4 = TupleGetChildProcessInfo(c3[l].Pid);

                                        if (c4 != null && c4.Count > 0)
                                        {
                                            childProcesses.AddRange(c4);

                                            if (childProcesses.Count >= MaxDescendants)
                                            {
                                                return childProcesses.Take(MaxDescendants).ToList();
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return childProcesses;
        }

        public override double GetProcessKvsLvidsUsagePercentage(string procName, int procId = -1)
        {
            // Not supported on Linux.
            return -1;
        }

        private List<(string ProcName, int Pid)> TupleGetChildProcessInfo(int processId)
        {
            string pidCmdResult = $"ps -o pid= --ppid {processId}".Bash();
            string procNameCmdResult = $"ps -o comm= --ppid {processId}".Bash();
            List<(string ProcName, int Pid)> childProcesses = null;

            if (!string.IsNullOrWhiteSpace(pidCmdResult) && !string.IsNullOrWhiteSpace(procNameCmdResult))
            {
                var sPids = pidCmdResult.Trim().Split(new char[] { '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
                var sProcNames = procNameCmdResult.Trim().Split(new char[] { '\n' }, System.StringSplitOptions.RemoveEmptyEntries);

                if (sPids?.Length > 0 && sProcNames?.Length > 0)
                {
                    childProcesses = new List<(string ProcName, int Pid)>();

                    for (int i = 0; i < sPids.Length; ++i)
                    {
                        if (sProcNames[i] == "ps" || sProcNames[i] == "bash")
                        {
                            continue;
                        }

                        if (int.TryParse(sPids[i], out int childProcId))
                        {
                            childProcesses.Add((sProcNames[i], childProcId));
                        }
                    }
                }
            }

            return childProcesses;
        }
    }
}