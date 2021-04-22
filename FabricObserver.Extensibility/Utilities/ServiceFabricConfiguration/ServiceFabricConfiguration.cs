// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// This class is used to read Service Fabric configuration.
    /// On Windows, the data is read from the registry (HKLM\SOFTWARE\Microsoft\Service Fabric)
    /// On Linux, the config values are stored in single line files in /etc/servicefabric directory.
    /// </summary>
    public abstract class ServiceFabricConfiguration
    {
        private static ServiceFabricConfiguration instance;
        private static readonly object lockObj = new object();

        public static ServiceFabricConfiguration Instance
        {
            get
            {
                if (instance == null)
                {
                    lock (lockObj)
                    {
                        if (instance == null)
                        {
                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            {
                                instance = new WindowsServiceFabricConfiguration();
                            }
                            else
                            {
                                instance = new LinuxServiceFabricConfiguration();
                            }
                        }
                    }
                }

                return instance;
            }
        }

        public string CompatibilityJsonPath => ReadStringValue();

        public bool DisableKernelDrivers => ReadInt32() != 0;

        public bool EnableCircularTraceSession => ReadInt32() != 0;

        public bool EnableUnsupportedPreviewFeatures => ReadInt32() != 0;

        public string FabricBinRoot => ReadStringValue();

        public string FabricCodePath => ReadStringValue();

        public string FabricDataRoot => ReadStringValue();

        public string FabricDnsServerIPAddress => ReadStringValue();

        public string FabricLogRoot => ReadStringValue();

        public bool IsSFVolumeDiskServiceEnabled => ReadInt32() != 0;

        public string NodeLastBootUpTime => ReadStringValue();

        public bool SfInstalledMoby => ReadInt32() != 0;

        public string UpdaterServicePath => ReadStringValue();

        public abstract string FabricVersion
        {
            get;
        }

        public abstract string FabricRoot
        {
            get;
        }

        public abstract string GetString(string name);

        public abstract int GetInt32(string name);

        private string ReadStringValue([CallerMemberName] string propertyName = null) => GetString(propertyName);

        private int ReadInt32([CallerMemberName] string propertyName = null) => GetInt32(propertyName);
    }
}
