// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Newtonsoft.Json;
using System;
using System.Diagnostics.Tracing;

namespace FabricObserver.Observers.Utilities.Telemetry
{
    [EventData]
    [Serializable]
    public class DiskTelemetryData : TelemetryDataBase
    {
        [EventField]
        public string DriveName 
        { 
            get; set; 
        }
        [EventField]
        public string FolderName 
        { 
            get; set; 
        }

        [JsonConstructor]
        public DiskTelemetryData() : base()
        {

        }  
    }
}
