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
    internal abstract class ServiceFabricConfiguration
    {
        private static ServiceFabricConfiguration instance;

        private static object lockObj = new object();

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

        public string CompatibilityJsonPath => this.ReadStringValue();

        public bool DisableKernelDrivers => this.ReadInt32() != 0;

        public bool EnableCircularTraceSession => this.ReadInt32() != 0;

        public bool EnableUnsupportedPreviewFeatures => this.ReadInt32() != 0;

        public string FabricBinRoot => this.ReadStringValue();

        public string FabricCodePath => this.ReadStringValue();

        public string FabricDataRoot => this.ReadStringValue();

        public string FabricDnsServerIPAddress => this.ReadStringValue();

        public string FabricLogRoot => this.ReadStringValue();

        public bool IsSFVolumeDiskServiceEnabled => this.ReadInt32() != 0;

        public string NodeLastBootUpTime => this.ReadStringValue();

        public bool SfInstalledMoby => this.ReadInt32() != 0;

        public string UpdaterServicePath => this.ReadStringValue();

        public abstract string FabricVersion { get; }

        public abstract string FabricRoot { get; }

        public abstract string GetString(string name);

        public abstract int GetInt32(string name);

        private string ReadStringValue([CallerMemberName] string propertyName = null) => this.GetString(propertyName);

        private int ReadInt32([CallerMemberName] string propertyName = null) => this.GetInt32(propertyName);
    }
}
