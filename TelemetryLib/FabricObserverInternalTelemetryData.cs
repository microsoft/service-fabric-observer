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
        public TimeSpan UpTime 
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

        public string EnabledObservers 
        { 
            get; set; 
        }

        public List<ObserverData> ObserverData 
        { 
            get; set; 
        }

        public bool IsInternalCluster 
        { 
            get; set; 
        }
    }
}
