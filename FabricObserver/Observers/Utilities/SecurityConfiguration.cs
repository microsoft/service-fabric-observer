// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Observers.Utilities
{
    public class SecurityConfiguration
    {
        public SecurityType SecurityType
        {
            get; set;
        }

        public string ClusterCertThumbprintOrCommonName
        {
            get; set;
        }

        public string ClusterCertSecondaryThumbprint
        {
            get; set;
        }
    }
}