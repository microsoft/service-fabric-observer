// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricObserver.Observers.Utilities;

namespace FabricObserver.Observers.MachineInfoModel
{
    public class Endpoint
    {
        public string HostName
        {
            get; set;
        }

        public int Port
        {
            get; set;
        }

        public DirectInternetProtocol Protocol
        {
            get; set;
        }
    }
}