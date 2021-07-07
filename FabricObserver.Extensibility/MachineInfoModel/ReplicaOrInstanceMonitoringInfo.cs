// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FabricObserver.Observers.MachineInfoModel
{
    public class ReplicaOrInstanceMonitoringInfo
    {
        public Uri ApplicationName
        {
            get; set;
        }

        public string ApplicationTypeName
        {
            get; set;
        }

        public string ContainerId
        {
            get; set;
        }

        public long HostProcessId
        {
            get; set;
        }

        public Guid PartitionId
        {
            get; set;
        }

        public long ReplicaOrInstanceId
        {
            get; set;
        }

        public Uri ServiceName
        {
            get; set;
        }

        public string ServicePackageActivationId
        {
            get; set;
        }

        public List<(string procName, int Pid)> ChildProcessInfo
        {
            get; set;
        }
    }
}
