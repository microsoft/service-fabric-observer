// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
namespace FabricObserverTests
{
    internal class HealthReportNotFoundException : Exception
    {
        public HealthReportNotFoundException(string message) : base(message)
        {

        }
    }
}