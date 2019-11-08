// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;

namespace Microsoft.ServiceFabric.TelemetryLib
{
    /// <summary>
    /// Telemetry doesn't have an eventsource of its own. This interface declares the bare minimum events,
    /// which should be implemented by eventsource of components which want to use Telemtry
    /// </summary>
    public interface ITelemetryEventSource
    {
        void VerboseMessage(string message, params object[] args);

        void FabricObserverRuntimeNodeEvent(
            string clusterId,
            string applicationVersion,
            string foConfigInfo,
            string foHealthInfo);
    }
}
