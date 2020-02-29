// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Fabric.Health;

namespace FabricObserver.Observers.Utilities
{
    internal class ConnectionState
    {
        public string HostName { get; set; }

        public bool Connected { get; set; }

        public HealthState Health { get; set; }
    }
}