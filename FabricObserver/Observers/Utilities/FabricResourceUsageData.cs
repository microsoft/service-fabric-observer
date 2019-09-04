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
        /// <summary>
        /// Initializes a new instance of the <see cref="FabricResourceUsageData{T}"/> class.
        /// </summary>
        /// <param name="property">Metric string...</param>
        /// <param name="id">Instance id...</param>
        public FabricResourceUsageData(string property, string id)
        {
            if (string.IsNullOrEmpty(property))
            {
                throw new ArgumentException($"Must provide a non-empty {property}...");
            }

            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException($"Must provide a non-empty {id}...");
            }

            this.Data = new List<T>();
            this.Property = property;
            this.Id = id;

            if (property.ToLower().Contains("cpu") || property.ToLower().Contains("disk space"))
            {
                this.Units = "%";
            }
            else if (property.ToLower().Contains("memory"))
            {
                this.Units = "MB";
            }
        }

        public string Property { get; }

        public string Id { get; }

        public string Units { get; }

        public List<T> Data { get; }

        private bool isInWarningState = false;

        /// <summary>
        /// Gets count of warnings per observer instance across iterations for the lifetime of the Observer.
        /// </summary>
        public int LifetimeWarningCount { get; private set; } = 0;

        public T MaxDataValue
        {
            get
            {
                if (this.Data?.Count > 0)
                {
                    return this.Data.Max();
                }

                return default(T);
            }
        }

        public double AverageDataValue
        {
            get
            {
                var average = 0.0;

                var v = this.Data as List<long>;
                if (v?.Count > 0)
                {
                    average = v.Average();
                }

                var x = this.Data as List<int>;
                if (x?.Count > 0)
                {
                    average = x.Average();
                }

                var y = this.Data as List<float>;
                if (y?.Count > 0)
                {
                    average = Convert.ToDouble(y.Average());
                }

                var z = this.Data as List<double>;
                if (z?.Count > 0)
                {
                    average = z.Average();
                }

                return average;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether there is an active warning state on this instance.
        /// Set to false when warning state changes to Ok...
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
                    this.LifetimeWarningCount++;
                }
            }
        }

        public bool IsUnhealthy<TU>(TU threshold)
        {
            if (this.Data.Count < 1 || Convert.ToDouble(threshold) < 1)
            {
                return false;
            }

            if (this.AverageDataValue >= Convert.ToDouble(threshold))
            {
                return true;
            }

            return false;
        }

        public double StandardDeviation
        {
            get
            {
                if (this.Data?.Count > 0)
                {
                    return Statistics.StandardDeviation(this.Data);
                }

                return 0;
            }
        }
    }
}
