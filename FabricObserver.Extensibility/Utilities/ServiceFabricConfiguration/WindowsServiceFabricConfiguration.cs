// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Microsoft.Win32;
using System;
using System.IO;
using System.Security;

namespace FabricObserver.Observers.Utilities
{
    public class WindowsServiceFabricConfiguration : ServiceFabricConfiguration
    {
        private const string ServiceFabricWindowsRegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Service Fabric";

        public override string FabricVersion => GetString(nameof(FabricVersion));

        public override string FabricRoot => GetString(nameof(FabricRoot));

        public override string GetString(string name)
        {
            try
            {
                return (string)Registry.GetValue(ServiceFabricWindowsRegistryPath, name, null);
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is SecurityException)
            {
                return "Unknown";
            }
        }

        public override int GetInt32(string name)
        {
            try
            { 
                return (int)Registry.GetValue(ServiceFabricWindowsRegistryPath, name, 0);
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is SecurityException)
            {
                return 0;
            }
        }
    }
}
