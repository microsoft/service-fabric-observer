using Aggregator.Data;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;

namespace Aggregator.Collectors
{
    public class ClusterCollector : CollectorBase<ClusterData>
    {
        public ClusterCollector(ServiceContext context, CancellationToken cancellationToken)
            : base(context, cancellationToken) 
        { 
        
        }

        protected override async Task<ClusterData> CollectData()
        {
            Counts counts = await SFUtilities.Instance.GetDeployedCountsAsync();
            var (totalMiliseconds, _) = SFUtilities.TupleGetTime();
            var data = new ClusterData(totalMiliseconds, counts);

            return data;
        }
    }
}
