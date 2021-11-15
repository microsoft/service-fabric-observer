using Aggregator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Services.Remoting.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace API.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class DataController : ControllerBase
    {

        

        public DataController()
        {
            
        }

        [HttpGet]
        public async Task<string> Get([FromQuery]string NodeName)
        {
            string response = "";

            var AggregatorProxy = ServiceProxy.Create<IMyCommunication>(
                    new Uri("fabric:/Internship/Aggregator"),
                    new Microsoft.ServiceFabric.Services.Client.ServicePartitionKey(0)
                    );

            List<Data> list=await AggregatorProxy.GetDataRemote(NodeName);
            foreach(var data in list)
            {
                response += data.ToString();
            }
            return response;
        }



        [HttpGet]
        [Route("info")]
        public async Task<string> Info([FromQuery] string NodeName)
        {
            return "A";
        }
        [HttpGet]
        [Route("infoo")]
        public async Task<string> Infoo([FromQuery] string NodeName)
        {
            return "B";
        }
    }
}
