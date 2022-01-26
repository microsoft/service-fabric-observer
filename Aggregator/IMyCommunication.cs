using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Services.Remoting;

namespace Aggregator
{
    public interface IMyCommunication : IService
    {
        Task PutDataRemote(string queueName,byte[] data);

        Task<List<byte[]>> GetDataRemote(string queueName);

        Task<List<Snapshot>> GetSnapshotsRemote(double milisecondsLow, double milisecondsHigh);

        Task DeleteAllSnapshotsRemote();
    }
}
