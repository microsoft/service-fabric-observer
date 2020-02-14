// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;

namespace FabricObserver.Utilities
{
    public sealed class Statistics
    {
        public static double StandardDeviation<T>(List<T> data)
        {
            var average = 0.0;
            List<double> squaredMeanDifferences = new List<double>();

            var v = data as List<long>;
            if (v != null && v.Count > 0)
            {
                average = v.Average();
                squaredMeanDifferences.AddRange(from n in v
                                                select (n - average) * (n - average));
            }

            var x = data as List<int>;
            if (x != null && x.Count > 0)
            {
                average = x.Average();
                squaredMeanDifferences.AddRange(from n in x
                                                select (n - average) * (n - average));
            }

            var y = data as List<float>;
            if (y != null && y.Count > 0)
            {
                average = Convert.ToDouble(y.Average());
                squaredMeanDifferences.AddRange(from n in y
                                                select (n - average) * (n - average));
            }

            var z = data as List<double>;
            if (z != null && z.Count > 0)
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

        public static double StandardDeviation2(IEnumerable<int> sequence)
        {
            if (sequence == null)
            {
                return -1;
            }

            double sum = 0;
            double sumOfSquares = 0;
            double count = 0;

            foreach (var item in sequence)
            {
                count++;
                sum += item;
                sumOfSquares += item * item;
            }

            var variance = sumOfSquares - (sum * sum / count);

            return Math.Sqrt(variance / count);
        }

        public static double StandardDeviation3(IEnumerable<double> sequence)
        {
            if (sequence == null)
            {
                return -1;
            }

            var computation = ComputeSumAndSumOfSquares(sequence);

            var variance = computation.SumOfSquares - (computation.Sum * computation.Sum / computation.Count);

            return Math.Sqrt(variance / computation.Count);
        }

        private static (int Count, double Sum, double SumOfSquares)
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

        internal static double StandardDeviation(List<long> sequence)
        {
            var mean = sequence.Average();
            var squaredMeanDifferences = from n in sequence
                                         select (n - mean) * (n - mean);
            var meanOfSquaredDifferences = squaredMeanDifferences.Average();

            var standardDeviation = Math.Sqrt(meanOfSquaredDifferences);

            return standardDeviation;
        }
    }
}
