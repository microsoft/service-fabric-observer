using System;
using System.Collections.Generic;
using System.Text;

namespace Aggregator
{
    public class AverageDictionary
    {
        private Dictionary<string, List<double>> dic=new Dictionary<string, List<double>>();

        public void addValue(string variableName,double value)
        {
            if (dic.ContainsKey(variableName))
            {
                dic[variableName].Add(value);
            }
            else
            {
                var l = new List<double>();
                l.Add(value);
                dic.Add(variableName, l);
            }
        }


        public double getAverage(string variableName)
        {
            var list = dic[variableName];
            double sum = 0;
            foreach (var e in list) sum += e;
            return sum / list.Count;
        }
    }
}
