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
    }

    public class AppServiceObserverData : ObserverData
    {
        public int MonitoredAppCount
        {
            get; set;
        }

        public int MonitoredServiceProcessCount
        {
            get; set;
        }
    }
}
