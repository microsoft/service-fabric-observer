using System.Collections.Generic;

namespace Aggregator.Data
{
    public interface DataBase<DerivedClass>
    {
        /// <summary>
        /// Every class that implements the DataBase interface should be makred as [Serializable]
        /// AverageData should be a static method, but we can't force classes to implement static methods so we do it like this
        /// </summary>
        /// <param name="list"></param>
        /// <returns></returns>
        DerivedClass AverageData(List<DerivedClass> list);
    }
}
