using Microsoft.Extensions.DependencyInjection;

namespace FabricObserver
{
    public interface IFabricObserverStartup
    {
        void ConfigureServices(IServiceCollection services);
    }
}
