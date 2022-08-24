// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Runtime.Serialization;

namespace FabricObserver.Observers.Utilities
{
    [Serializable]
    public class LinuxPermissionException : Exception
    {
        public LinuxPermissionException()
        {
        }

        public LinuxPermissionException(string message) : base(message)
        {
        }

        public LinuxPermissionException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected LinuxPermissionException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}