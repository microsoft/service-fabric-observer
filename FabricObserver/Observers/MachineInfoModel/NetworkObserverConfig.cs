// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;

namespace FabricObserver.Observers.MachineInfoModel
{
    public class NetworkObserverConfig
    {
        public string TargetApp
        {
            get; set;
        }

        public List<Endpoint> Endpoints
        {
            get; set;
        }
    }
}
