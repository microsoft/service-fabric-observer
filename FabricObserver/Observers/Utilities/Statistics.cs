// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;

namespace FabricObserver.Utilities
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

        internal static double StandardDeviation<T>(List<T> data)
        {
            var average = 0.0;
            var squaredMeanDifferences = new List<double>();

            if (data is List<long> v && v.Count > 0)
            {
                average = v.Average();
                squaredMeanDifferences.AddRange(from n in v
                                                select (n - average) * (n - average));
            }

            if (data is List<int> x && x.Count > 0)
            {
                average = x.Average();
                squaredMeanDifferences.AddRange(from n in x
                                                select (n - average) * (n - average));
            }

            if (data is List<float> y && y.Count > 0)
            {
                average = Convert.ToDouble(y.Average());
                squaredMeanDifferences.AddRange(from n in y
                                                select (n - average) * (n - average));
            }

            if (data is List<double> z && z.Count > 0)
            {
                average = z.Average();
                squaredMeanDifferences.AddRange(from n in z
                                                select (n - average) * (n - average));
            }

            // Find the mean of those squared differences:
            var meanOfSquaredDifferences = squaredMeanDifferences.Average();

            // Standard Deviation is the square root of that mean:
            var standardDeviation = Math.Sqrt(meanOfSquaredDifferences);

            return standardDeviation;
        }

        internal static double StandardDeviation(List<long> sequence)
        {
            var mean = sequence.Average();

            var squaredMeanDifferences = from n in sequence
                                         select (n - mean) * (n - mean);

            var meanOfSquaredDifferences = squaredMeanDifferences.Average();

            var standardDeviation = Math.Sqrt(meanOfSquaredDifferences);

            return standardDeviation;
        }

        internal List<int> SlidingWindow(
                    List<int> data,
                    int kwidth,
                    WindowType windowType)
        {
            if (kwidth < 1 || data.Count == 0)
            {
                return null;
            }

            var map = new Dictionary<int, int>(data.Count);
            var window = new List<int> { data.Count - kwidth + 1 };
            var bst = new SortedSet<int>();

            for (int i = 0; i < data.Count; i++)
            {
                var visit = data[i];
                bst.Add(visit);
                map[visit] = i;

                if (i < kwidth - 1)
                {
                    continue;
                }

                if (i >= kwidth && map[data[i - kwidth]] == (i - kwidth))
                {
                    int k = data[i - kwidth];
                    bst.Remove(k);
                    map.Remove(k);
                }

                if (windowType == WindowType.Max)
                {
                    window.Insert(i - kwidth + 1, bst.Max);
                }
                else
                {
                    window.Insert(i - kwidth + 1, bst.Min);
                }
            }

            return window;
        }
    }
}
