// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace FabricObserver.Observers.Utilities
{
    public enum WindowType
    {
        Max,
        Min
    }

    /// <summary>
    /// Small statistical utilities class.
    /// </summary>
    public static class Statistics
    {
        /// <summary>
        /// Computes the standard deviation of type T of a List of numeric values of type T.
        /// </summary>
        /// <typeparam name="T">Numeric type T.</typeparam>
        /// <param name="data">List of numeric values of type T.</param>
        /// <returns>Standard deviation of input type T as type T.
        /// Consumption: See impl of StandardDeviation member of FabricResourceUsageData class.</returns>
        public static T StandardDeviation<T>(IEnumerable<T> data) where T : struct
        {
            var squaredMeanDifferences = new List<T>();
            T meanOfSquaredDifferences;
            var standardDeviation = default(T);
            double average;

            switch (data)
            {
                case IList<long> value when value.Count > 0:
                    average = value.Average();
                    squaredMeanDifferences.AddRange(value.Select(n => (T)Convert.ChangeType((n - average) * (n - average), typeof(T))));
                    meanOfSquaredDifferences = (T)Convert.ChangeType((squaredMeanDifferences as List<long>)?.Average(), typeof(T));
                    standardDeviation = (T)Convert.ChangeType(Math.Sqrt(Convert.ToInt64(meanOfSquaredDifferences)), typeof(T));
                    break;

                case IList<int> value when value.Count > 0:
                    average = value.Average();
                    squaredMeanDifferences.AddRange(value.Select(n => (T)Convert.ChangeType((n - average) * (n - average), typeof(T))));
                    meanOfSquaredDifferences = (T)Convert.ChangeType((squaredMeanDifferences as List<int>)?.Average(), typeof(T));
                    standardDeviation = (T)Convert.ChangeType(Math.Sqrt(Convert.ToInt32(meanOfSquaredDifferences)), typeof(T));
                    break;

                case IList<float> value when value.Count > 0:
                    average = value.Average();
                    squaredMeanDifferences.AddRange(value.Select(n => (T)Convert.ChangeType((n - average) * (n - average), typeof(T))));
                    meanOfSquaredDifferences =  (T)Convert.ChangeType((squaredMeanDifferences as List<float>)?.Average(), typeof(T));
                    standardDeviation = (T)Convert.ChangeType(Math.Sqrt(Convert.ToSingle(meanOfSquaredDifferences)), typeof(T));
                    break;

                case IList<double> value when value.Count > 0:
                    average = value.Average();
                    squaredMeanDifferences.AddRange(value.Select(n => (T)Convert.ChangeType((n - average) * (n - average), typeof(T))));
                    meanOfSquaredDifferences = (T)Convert.ChangeType((squaredMeanDifferences as List<double>)?.Average(), typeof(T));
                    standardDeviation = (T)Convert.ChangeType(Math.Sqrt(Convert.ToDouble(meanOfSquaredDifferences)), typeof(T));
                    break;

                case ConcurrentQueue<long> value when value.Count > 0:
                    average = value.Average();
                    squaredMeanDifferences.AddRange(value.Select(n => (T)Convert.ChangeType((n - average) * (n - average), typeof(T))));
                    meanOfSquaredDifferences = (T)Convert.ChangeType((squaredMeanDifferences as List<long>)?.Average(), typeof(T));
                    standardDeviation = (T)Convert.ChangeType(Math.Sqrt(Convert.ToInt64(meanOfSquaredDifferences)), typeof(T));
                    break;

                case ConcurrentQueue<int> value when value.Count > 0:
                    average = value.Average();
                    squaredMeanDifferences.AddRange(value.Select(n => (T)Convert.ChangeType((n - average) * (n - average), typeof(T))));
                    meanOfSquaredDifferences = (T)Convert.ChangeType((squaredMeanDifferences as List<int>)?.Average(), typeof(T));
                    standardDeviation = (T)Convert.ChangeType(Math.Sqrt(Convert.ToInt32(meanOfSquaredDifferences)), typeof(T));
                    break;

                case ConcurrentQueue<float> value when value.Count > 0:
                    average = value.Average();
                    squaredMeanDifferences.AddRange(value.Select(n => (T)Convert.ChangeType((n - average) * (n - average), typeof(T))));
                    meanOfSquaredDifferences = (T)Convert.ChangeType((squaredMeanDifferences as List<float>)?.Average(), typeof(T));
                    standardDeviation = (T)Convert.ChangeType(Math.Sqrt(Convert.ToSingle(meanOfSquaredDifferences)), typeof(T));
                    break;

                case ConcurrentQueue<double> value when value.Count > 0:
                    average = value.Average();
                    squaredMeanDifferences.AddRange(value.Select(n => (T)Convert.ChangeType((n - average) * (n - average), typeof(T))));
                    meanOfSquaredDifferences = (T)Convert.ChangeType((squaredMeanDifferences as List<double>)?.Average(), typeof(T));
                    standardDeviation = (T)Convert.ChangeType(Math.Sqrt(Convert.ToDouble(meanOfSquaredDifferences)), typeof(T));
                    break;
            }

            // Standard Deviation is the square root of the mean of squared differences.
            return (T)Convert.ChangeType(standardDeviation, typeof(T));
        }

        /// <summary>
        /// Computes Max or Min values within left-to-right sliding window of elements
        /// of width windowWidth in a numeric list of type T.
        /// </summary>
        /// <typeparam name="T">Type of numeric value in List.</typeparam>
        /// <param name="data">List of some numeric type T.</param>
        /// <param name="windowWidth">Number of elements inside a window.</param>
        /// <param name="windowType">Minimum or Maximum sliding window sort.</param>
        /// <returns>List of sliding window sorted elements of numeric type T.</returns>
        public static IList<T> SlidingWindow<T>(IEnumerable<T> data, int windowWidth, WindowType windowType) where T : struct
        {
            if (windowWidth < 1 || data.Count() == 0)
            {
                return null;
            }

            var map = new Dictionary<T, T>(data.Count());
            var capacity = data.Count() - (windowWidth + 1);
            var windowList = new List<T>(capacity);
            var bst = new SortedSet<T>();

            for (var i = 0; i < capacity; i++)
            {
                var visit = data.ElementAt(i);
                _ = bst.Add(visit);
                map[visit] = (T)Convert.ChangeType(i, typeof(T));

                if (i < windowWidth - 1)
                {
                    continue;
                }

                if (i >= windowWidth)
                {
                    var y = map[data.ElementAt(i - windowWidth)];

                    if (Equals(y, (T)Convert.ChangeType(i - windowWidth, typeof(T))))
                    {
                        var k = data.ElementAt(i - windowWidth);
                        _ = bst.Remove(k);
                        _ = map.Remove(k);
                    }
                }

                windowList.Insert(i - windowWidth + 1, windowType == WindowType.Max ? bst.Max : bst.Min);
            }

            return windowList;
        }
    }
}