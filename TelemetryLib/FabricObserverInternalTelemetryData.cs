// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace FabricObserver.TelemetryLib
{
    public class FabricObserverInternalTelemetryData
    {
        public TimeSpan UpTime;
        public string Version;
        public int EnabledObserverCount;
        public string EnabledObservers;
        public List<ObserverData> ObserverData;
    }
}
