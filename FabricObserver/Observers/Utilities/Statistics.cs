// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;

namespace FabricObserver.Observers.Utilities
{
    public enum WindowType
    {
        Max,
        Min,
    }

    public sealed class Statistics
    {
        internal static (int Count, double Sum, double SumOfSquares)
            ComputeSumAndSumOfSquares(IEnumerable<double> sequence)
        {
            double sum = 0;
            double sumOfSquares = 0;
            int count = 0;

            foreach (var item in sequence)
            {
                count++;
                sum += item;
                sumOfSquares += item * item;
            }

            return (count, sum, sumOfSquares);
        }

        /// <summary>
        /// Computes the standard deviation of type T of a List of numeric values of type T.
        /// </summary>
        /// <typeparam name="T">Numeric type T.</typeparam>
        /// <param name="data">List of numeric values of type T.</param>
        /// <returns>Standard deviation of input type T as type T.
        /// Consumption: See impl of StandardDeviation member of FabricResourceUsageData class.</returns>
        internal static T StandardDeviation<T>(List<T> data)
            where T : struct
        {
            var squaredMeanDifferences = new List<T>();
            T meanOfSquaredDifferences;
            var standardDeviation = default(T);
            double average;

            switch (data)
            {
                case List<long> value when value.Count > 0:
                    average = value.Average();
                    squaredMeanDifferences.AddRange(
                        value.Select(
                            n => (T)Convert.ChangeType(
                                (n - average) * (n - average), typeof(T))));

                    meanOfSquaredDifferences = (T)Convert.ChangeType(
                        (squaredMeanDifferences as List<long> ?? throw new InvalidOperationException()).Average(), typeof(T));

                    // Standard Deviation is the square root of the mean of squared differences:
                    standardDeviation = (T)Convert.ChangeType(
                        Math.Sqrt(Convert.ToInt64(meanOfSquaredDifferences)), typeof(T));

                    break;

                case List<int> value when value.Count > 0:
                    average = value.Average();
                    squaredMeanDifferences.AddRange(
                        value.Select(
                            n => (T)Convert.ChangeType(
                                (n - average) * (n - average), typeof(T))));

                    meanOfSquaredDifferences = (T)Convert.ChangeType(
                        (squaredMeanDifferences as List<int> ?? throw new InvalidOperationException()).Average(), typeof(T));

                    standardDeviation = (T)Convert.ChangeType(
                        Math.Sqrt(Convert.ToInt32(meanOfSquaredDifferences)), typeof(T));

                    break;

                case List<float> value when value.Count > 0:
                    average = value.Average();
                    squaredMeanDifferences.AddRange(
                        value.Select(
                            n => (T)Convert.ChangeType(
                                (n - average) * (n - average), typeof(T))));

                    meanOfSquaredDifferences = (T)Convert.ChangeType(
                        (squaredMeanDifferences as List<float> ?? throw new InvalidOperationException()).Average(), typeof(T));

                    standardDeviation = (T)Convert.ChangeType(
                        Math.Sqrt(Convert.ToSingle(meanOfSquaredDifferences)), typeof(T));

                    break;

                case List<double> value when value.Count > 0:
                    average = value.Average();
                    squaredMeanDifferences.AddRange(
                        value.Select(
                            n => (T)Convert.ChangeType(
                                (n - average) * (n - average), typeof(T))));

                    meanOfSquaredDifferences = (T)Convert.ChangeType(
                        (squaredMeanDifferences as List<double> ?? throw new InvalidOperationException()).Average(), typeof(T));

                    standardDeviation = (T)Convert.ChangeType(
                        Math.Sqrt(Convert.ToDouble(meanOfSquaredDifferences)), typeof(T));

                    break;
            }

            return (T)Convert.ChangeType(standardDeviation, typeof(T));
        }

        /// <summary>
        /// Computes Max or Min values within left-to-right sliding window of elements
        /// of width windowWidth in a generic numeric list of type T.
        /// </summary>
        /// <typeparam name="T">Type of numeric value in List.</typeparam>
        /// <param name="data">List of some numeric type.</param>
        /// <param name="windowWidth">Number of elements inside a window.</param>
        /// <param name="windowType">Minimum or Maximum sliding window sort.</param>
        /// <returns>List of sliding window sorted elements of numeric type T.</returns>
        internal static List<T> SlidingWindow<T>(
            List<T> data,
            int windowWidth,
            WindowType windowType)
                where T : struct
        {
            if (windowWidth < 1 || data.Count == 0)
            {
                return null;
            }

            var map = new Dictionary<T, T>(data.Count);
            var x = data.Count - (windowWidth + 1);
            var windowList = new List<T> { (T)Convert.ChangeType(x, typeof(T)) };
            var bst = new SortedSet<T>();

            for (var i = 0; i < x; i++)
            {
                var visit = data[i];
                _ = bst.Add(visit);
                map[visit] = (T)Convert.ChangeType(i, typeof(T));

                if (i < windowWidth - 1)
                {
                    continue;
                }

                if (i >= windowWidth)
                {
                    var y = map[data[i - windowWidth]];

                    if (Equals(y, (T)Convert.ChangeType(i - windowWidth, typeof(T))))
                    {
                        var k = data[i - windowWidth];
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
