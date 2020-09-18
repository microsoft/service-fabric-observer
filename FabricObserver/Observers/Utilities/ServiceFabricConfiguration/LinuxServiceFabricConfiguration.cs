// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.IO;
using System.Linq;

namespace FabricObserver.Observers.Utilities
{
    public class LinuxServiceFabricConfiguration : ServiceFabricConfiguration
    {
        public override string FabricVersion
        {
            get
            {
                string clusterVersionPath = Path.Combine(FabricCodePath, "ClusterVersion");
                string fabricVersion = null;

                if (File.Exists(clusterVersionPath))
                {
                    fabricVersion = File.ReadAllText(clusterVersionPath);
                }

                return fabricVersion;
            }
        }

        public override string FabricRoot => Path.GetDirectoryName(FabricBinRoot);

        public override int GetInt32(string name) => int.Parse(Read(name, defaultValue: "0"));

        public override string GetString(string name) => Read(name, null);

        private static string Read(string name, string defaultValue)
        {
            string value = defaultValue;
            string filePath = "/etc/servicefabric/" + name;

            if (File.Exists(filePath))
            {
                value = File.ReadAllLines(filePath).FirstOrDefault();
            }

            return value;
        }
    }
}
