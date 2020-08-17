using System;

namespace FabricObserver
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class FabricObserverStartupAttribute : Attribute
    {
        public FabricObserverStartupAttribute(Type startupType)
        {
            this.StartupType = startupType;
        }

        public Type StartupType
        {
            get;
        }
    }
}
