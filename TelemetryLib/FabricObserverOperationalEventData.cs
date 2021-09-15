// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FabricObserver.TelemetryLib
{
    public class FabricObserverOperationalEventData
    {
        public string UpTime
        {
            get; set;
        }

        public string Version
        {
            get; set;
        }

        public int EnabledObserverCount
        {
            get; set;
        }

        public bool HasPlugins
        {
            get; set;
        }

        public bool ParallelExecutionEnabled
        {
            get; set;
        }

        public List<ObserverData> ObserverData
        {
            get; set;
        }

        public string OS => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux";
    }
}
