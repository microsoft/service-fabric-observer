﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;

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

        public string SFRuntimeVersion
        {
            get; set;
        }

        public Dictionary<string, ObserverData> ObserverData
        {
            get; set;
        }

        public static string OS => OperatingSystem.IsWindows() ? "Windows" : "Linux";
    }
}
