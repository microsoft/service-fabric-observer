// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;

namespace FabricObserver.Utilities
{
    [Serializable]
    public class InvalidPluginException : Exception
    {
        public InvalidPluginException()
        {
        }

        public InvalidPluginException(string message) : base(message)
        {
        }

        public InvalidPluginException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
