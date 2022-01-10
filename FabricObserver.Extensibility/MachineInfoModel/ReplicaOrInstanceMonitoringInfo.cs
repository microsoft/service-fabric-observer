// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Query;

namespace FabricObserver.Observers.MachineInfoModel
{
    /// <summary>
    /// Replica information data-only class with properties are public get and set because it is expected that consumers may set them (like AppObserver, for example).
    /// </summary>
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

        public ServiceKind ServiceKind
        {
            get; set;
        }

        public string ServicePackageActivationId
        {
            get; set;
        }

        public ServiceReplicaStatus ReplicaStatus
        {
            get; set;
        }

        public List<(string procName, int Pid)> ChildProcesses
        {
            get; set;
        }

        public ReplicaRole ReplicaRole 
        { 
            get; set; 
        }
    }

    public enum ReplicaKind
    {
        Stateful,
        Stateless
    }
}
