using System;

namespace FabricObserver
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class FabricObserverStartupAttribute : Attribute
    {
        private readonly Type startupType;

        public FabricObserverStartupAttribute(Type startupType)
        {
            this.startupType = startupType;
        }

        public Type StartupType => this.startupType;
    }
}
