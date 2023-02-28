// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;

namespace FabricObserver.TelemetryLib
{
    public class ClusterObserverOperationalEventData
    {
        public string Version
        {
            get; set;
        }

        public static string OS => OperatingSystem.IsWindows() ? "Windows" : "Linux";
    }
}
