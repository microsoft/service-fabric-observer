using FabricObserver.Observers.Interfaces;
using Microsoft.Extensions.DependencyInjection;

[assembly: FabricObserver.FabricObserverStartup(typeof(FabricObserver.Observers.SampleNewObserverStartup))]
namespace FabricObserver.Observers
{
    public class SampleNewObserverStartup : IFabricObserverStartup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddScoped(typeof(IObserver), typeof(SampleNewObserver));
        }
    }
}
