// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;

namespace FabricObserver.Utilities
{
    public class FabricResourceUsageData<T>
    {
        private bool isInWarningState = false;
        public List<T> Data { get; private set; }
        public string Name { get; set; }
        /// <summary>
        /// Maintains count of warnings per observer instance across iterations for the lifetime of the Observer.
        /// </summary>
        public int LifetimeWarningCount { get; set; } = 0;
        public T MaxDataValue
        {
            get
            {
                if (Data?.Count > 0)
                {
                    return Data.Max();
                }

                return default(T);
            }
        }

        public double AverageDataValue
        {
            get
            {
                var average = 0.0;

                var v = Data as List<long>;
                if (v?.Count > 0)
                {
                    average = v.Average();
                }

                var x = Data as List<int>;
                if (x?.Count > 0)
                {
                    average = x.Average();
                }

                var y = Data as List<float>;
                if (y?.Count > 0)
                {
                    average = Convert.ToDouble(y.Average());
                }

                var z = Data as List<double>;
                if (z?.Count > 0)
                {
                    average = z.Average();
                }

                return average;
            }
        }

        /// <summary>
        /// Current active warning state on this instance.
        /// Reset to false when warning state changes to Ok...
        /// </summary>
        public bool ActiveErrorOrWarning
        {
            get
            {
                return this.isInWarningState;
            }
            set
            {
                this.isInWarningState = value;

                if (value == true)
                {
                    LifetimeWarningCount++;
                }
            }
        }


        public FabricResourceUsageData(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("Must provide a non-empty name...");
            }

            Data = new List<T>();
            Name = name;
        }

        public bool IsUnhealthy<TU>(TU threshold)
        {
            if (Data.Count < 1 || Convert.ToDouble(threshold) < 1)
            {
                return false;
            }

            if (AverageDataValue >= Convert.ToDouble(threshold))
            {
                return true;
            }

            return false;
        }

        public double StandardDeviation
        {
            get
            {
                if (Data?.Count > 0)
                {
                    return Statistics.StandardDeviation(Data);
                }
                return 0;
            }
        }
    }
}
