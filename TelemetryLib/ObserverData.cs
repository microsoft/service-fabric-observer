// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.TelemetryLib
{
    public class ObserverData
    {
        public string ObserverName
        {
            get; set;
        }

        public int ErrorCount
        {
            get; set;
        }

        public int WarningCount
        {
            get; set;
        }

        public string HealthState
        {
            get; set;
        }
    }

    public class AppServiceObserverData : ObserverData
    {
        // This is done for list data ordering purposes.
        public new string ObserverName
        {
            get; set;
        }

        public int MonitoredAppCount
        {
            get; set;
        }

        public int MonitoredServiceCount
        {
            get; set;
        }
    }
}
