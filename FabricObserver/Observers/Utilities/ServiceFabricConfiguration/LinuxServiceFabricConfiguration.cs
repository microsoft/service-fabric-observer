using System.IO;

namespace FabricObserver.Observers.Utilities
{
    internal class LinuxServiceFabricConfiguration : ServiceFabricConfiguration
    {
        public override string FabricVersion
        {
            get
            {
                string clusterVersionPath = Path.Combine(this.FabricCodePath, "ClusterVersion");
                string fabricVersion = null;

                if (File.Exists(clusterVersionPath))
                {
                    fabricVersion = File.ReadAllText(clusterVersionPath);
                }

                return fabricVersion;
            }
        }

        public override string FabricRoot => Path.GetDirectoryName(this.FabricBinRoot);

        public override int GetInt32(string name) => int.Parse(Read(name, defaultValue: "0"));

        public override string GetString(string name) => Read(name, null);

        private static string Read(string name, string defaultValue)
        {
            string value = defaultValue;
            string filePath = "/etc/servicefabric/" + name;

            if (File.Exists(filePath))
            {
                value = File.ReadAllText(filePath);
            }

            return value;
        }
    }
}
