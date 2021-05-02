// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// The "direct" protocol to use in NetworkObserver availability tests.
    /// Http is for REST-based calls or any endpoint that is directly reachable over HTTP.
    /// Tcp is for endpoints reachable only via direct TCP socket connections, like most database servers.
    /// </summary>
    public enum DirectInternetProtocol
    {
        /// <summary>
        /// For REST-based calls or any endpoint that is directly reachable over HTTP.
        /// </summary>
        Http,

        /// <summary>
        /// For endpoints reachable only via direct TCP socket connections, like most Windows database servers.
        /// </summary>
        Tcp
    }
}