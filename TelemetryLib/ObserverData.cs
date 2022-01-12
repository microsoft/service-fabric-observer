// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Newtonsoft.Json;

namespace FabricObserver.TelemetryLib
{
    [JsonObject]
    public class ObserverData
    {
        public int ErrorCount
        {
            get; set;
        }

        public int WarningCount
        {
            get; set;
        }

        public ServiceData ServiceData 
        { 
            get; set; 
        } 
    }

    public class ServiceData
    {
        public int MonitoredAppCount
        {
            get; set;
        }

        public int MonitoredServiceProcessCount
        {
            get; set;
        }

        public bool ConcurrencyEnabled
        {
            get; set;
        }
    }
}
