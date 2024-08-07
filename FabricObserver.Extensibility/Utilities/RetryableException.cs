﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;

namespace FabricObserver.Observers.Utilities
{
    [Serializable]
    public class RetryableException : Exception
    {
        public RetryableException()
        {
        }

        public RetryableException(string message) : base(message)
        {
        }

        public RetryableException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}