using System;
using System.Collections.Generic;
using System.Linq;

namespace Aggregator
{
    public class AverageDictionary
    {
        private readonly Dictionary<string, List<double>> dic = new Dictionary<string, List<double>>();

        public void AddValue(string variableName,double value)
        {
            if (dic.ContainsKey(variableName))
            {
                dic[variableName].Add(value);
            }
            else
            {
                var l = new List<double>
                {
                    value
                };

                dic.Add(variableName, l);
            }
        }

        public double GetAverage(string key)
        {
            if (dic.ContainsKey(key))
            {
                var list = dic[key];
                return list.Average();
            }

            throw new ArgumentException($"The supplied key, '{key}', does not exist in the dictionary.");
        }
    }
}
