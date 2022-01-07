using Aggregator.Data;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Aggregator.Collectors
{
    public class ClusterCollector : CollectorBase<ClusterData>
    {
        public ClusterCollector(ServiceContext context, CancellationToken cancellationToken) : base(context, cancellationToken) { }

        protected override async Task<ClusterData> CollectData()
        {
            Counts counts = await SFUtilities.Instance.GetDeployedCountsAsync();

            string nodeName = this.Context.NodeContext.NodeName;


            var (totalMiliseconds, _) = SFUtilities.getTime();
            var data = new ClusterData(totalMiliseconds, counts);
            return data;
        }
    }
}
