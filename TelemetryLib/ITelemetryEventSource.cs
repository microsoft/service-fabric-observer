// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.TelemetryLib
{
    public interface ITelemetryEventSource
    {
        void InternalFODataEvent<T>(T data);

        void InternalFOCriticalErrorDataEvent<T>(T data);
    }
}
