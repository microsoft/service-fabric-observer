// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;

namespace FabricObserver.Model
{
    public class ReplicaOrInstanceMonitoringInfo
    {
        public long HostProcessId { get; set; }

        public Uri ApplicationName { get; set; }

        public string ApplicationTypeName { get; set; }

        public long ReplicaOrInstanceId { get; set; }

        public Guid Partitionid { get; set; }
    }
}
