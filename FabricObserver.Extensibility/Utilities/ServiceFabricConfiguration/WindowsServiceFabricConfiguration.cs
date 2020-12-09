// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Microsoft.Win32;

namespace FabricObserver.Observers.Utilities
{
    public class WindowsServiceFabricConfiguration : ServiceFabricConfiguration
    {
        private const string SurviceFabricWindowsRegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Service Fabric";

        public override string FabricVersion => GetString(nameof(FabricVersion));

        public override string FabricRoot => GetString(nameof(FabricRoot));

        public override string GetString(string name)
        {
            return (string)Registry.GetValue(SurviceFabricWindowsRegistryPath, name, null);
        }

        public override int GetInt32(string name)
        {
            return (int)Registry.GetValue(SurviceFabricWindowsRegistryPath, name, 0);
        }
    }
}
