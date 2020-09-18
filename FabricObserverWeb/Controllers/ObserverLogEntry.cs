// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserverWeb.Controllers
{
    public class ObserverLogEntry
    {
        public string Date { get; set; }

        public string HealthState { get; set; }

        public string Message { get; set; }
    }
}