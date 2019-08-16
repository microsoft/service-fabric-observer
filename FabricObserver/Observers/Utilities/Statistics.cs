// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;

namespace FabricObserver.Utilities
{
    class Statistics
    {
        public static double StandardDeviation<T>(List<T> Data)
        {
            var average = 0.0;
            List<double> squaredMeanDifferences = new List<double>();

            var v = Data as List<long>;
            if (v != null && v.Count > 0)
            {
                average = v.Average();
                squaredMeanDifferences.AddRange(from n in v
                                                select (n - average) * (n - average));
            }

            var x = Data as List<int>;
            if (x != null && x.Count > 0)
            {
                average = x.Average();
                squaredMeanDifferences.AddRange(from n in x
                                                select (n - average) * (n - average));
            }

            var y = Data as List<float>;
            if (y != null && y.Count > 0)
            {
                average = Convert.ToDouble(y.Average());
                squaredMeanDifferences.AddRange(from n in y
                                                select (n - average) * (n - average));
            }

            var z = Data as List<double>;
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
            double sum = 0;
            double sumOfSquares = 0;
            double count = 0;

            foreach (var item in sequence)
            {
                count++;
                sum += item;
                sumOfSquares += item * item;
            }

            var variance = sumOfSquares - sum * sum / count;
            return Math.Sqrt(variance / count);
        }

        public static double StandardDeviation3(IEnumerable<double> sequence)
        {
            var computation = ComputeSumAndSumOfSquares(sequence);

            var variance = computation.Item3 - computation.Item2 * computation.Item2 / computation.Item1;
            return Math.Sqrt(variance / computation.Item1);
        }

        private static Tuple<int, double, double> ComputeSumAndSumOfSquares(IEnumerable<double> sequence)
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

            return new Tuple<int, double, double>(count, sum, sumOfSquares);
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
