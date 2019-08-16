// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;

namespace FabricObserver.Model
{
    public class NetworkObserverConfig
    {
        public string AppTarget { get; set; }
        public List<Endpoint> Endpoints { get; set; }
    }

    public class Endpoint
    {
        public string HostName { get; set; }
        public int Port { get; set; }
    }
}
