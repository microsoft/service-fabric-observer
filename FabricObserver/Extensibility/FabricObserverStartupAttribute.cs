// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;

namespace FabricObserver
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class FabricObserverStartupAttribute : Attribute
    {
        public FabricObserverStartupAttribute(Type startupType)
        {
            StartupType = startupType;
        }

        public Type StartupType
        {
            get;
        }
    }
}
