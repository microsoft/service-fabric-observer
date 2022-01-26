using System.Collections.Generic;

namespace Aggregator
{
    public class DictionaryList<TKey,TValue>
    {
        private readonly Dictionary<TKey,List<TValue>> dic = new Dictionary<TKey,List<TValue>>();

        public void Add(TKey key, TValue value)
        {
            if (dic.ContainsKey(key))
            {
                dic[key].Add(value);
            }
            else
            {
                List<TValue> list = new List<TValue>
                {
                    value
                };

                dic.Add(key, list);
            }
        }

        public Dictionary<TKey, List<TValue>>.KeyCollection GetKeys()
        {
             return dic.Keys;
        }

        public List<TValue> GetList(TKey key)
        {
            if (dic.ContainsKey(key))
            {
                return dic[key];
            }

            return null;
        }
    }
}
