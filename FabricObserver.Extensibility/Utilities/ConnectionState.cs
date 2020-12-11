// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Fabric.Health;

namespace FabricObserver.Observers.Utilities
{
    public class ConnectionState
    {
        public bool Connected
        {
            get; set;
        }

        public HealthState Health
        {
            get; set;
        }

        public string HostName
        {
            get; set;
        }

        public string TargetApp
        {
            get; set;
        }
    }
}