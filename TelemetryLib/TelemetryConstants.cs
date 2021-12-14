// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.TelemetryLib
{
    internal static class TelemetryConstants
    {
        internal const int AsyncOperationTimeoutSeconds = 120;
        internal const string Undefined = "undefined";
        internal const string ClusterTypeStandalone = "standalone";
        internal const string ClusterTypeSfrp = "SFRP";
        internal const string ClusterTypePaasV1 = "PaasV1";
        internal const string ConnectionString = "InstrumentationKey=c065641b-ec84-43fe-a8e7-c2bcbb697995;IngestionEndpoint=https://eastus-0.in.applicationinsights.azure.com/";
    }
}