// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.ServiceFabric.TelemetryLib
{
    internal static class TelemetryConstants
    {
        internal const string AppInsightsInstrumentationKey = "$Token$";
        internal const string TelemetryEndpoint = "https://vortex.data.microsoft.com/collect/v1";
        internal const string Undefined = "undefined";
        internal const string ClusterTypeStandalone = "standalone";
        internal const string ClusterTypeSfrp = "SFRP";
        internal const string ClusterTypePaasV1 = "PaasV1";
    }
}
