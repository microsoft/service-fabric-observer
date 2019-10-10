namespace FabricObserver
{
    public class SecurityConfiguration
    {
        public SecurityType SecurityType { get; set; }

        public string ClusterCertThumbprintOrCommonName { get; set; }

        public string ClusterCertSecondaryThumbprint { get; set; }
    }
}