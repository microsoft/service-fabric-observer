// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Runtime.InteropServices;

namespace FabricObserver.TelemetryLib
{
    public class CriticalErrorEventData
    {
        public string Version
        {
            get; set;
        }

        public string Source
        {
            get; set;
        }

        public string ErrorMessage
        {
            get; set;
        }

        public string ErrorStack
        {
            get; set;
        }

        public string CrashTime
        {
            get; set;
        }

        public string SFRuntimeVersion
        {
            get; set;
        }

        public string OS => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux";
    }
}
