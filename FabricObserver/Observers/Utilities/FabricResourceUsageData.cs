// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;

namespace FabricObserver.Observers.Utilities
{
    public class FabricResourceUsageData<T>
            where T : struct
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FabricResourceUsageData{T}"/> class.
        /// </summary>
        /// <param name="property">Metric string.</param>
        /// <param name="id">Instance id.</param>
        public FabricResourceUsageData(string property, string id)
        {
            if (string.IsNullOrEmpty(property))
            {
                throw new ArgumentException($"Must provide a non-empty {property}.");
            }

            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException($"Must provide a non-empty {id}.");
            }

            this.Data = new List<T>();
            this.Property = property;
            this.Id = id;
            this.Units = string.Empty;

            if (property.Contains("MB"))
            {
                this.Units = "MB";
            }

            if (property.ToLower().Contains("%") ||
                property.ToLower().Contains("cpu") ||
                property.ToLower().Contains("percent"))
            {
                this.Units = "%";
            }
        }

        public string Property { get; }

        public string Id { get; }

        public string Units { get; }

        public List<T> Data { get; }

        private bool isInWarningState;

        /// <summary>
        /// Gets count of warnings per observer instance across iterations for the lifetime of the Observer.
        /// </summary>
        public int LifetimeWarningCount { get; private set; }

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
                double average = 0.0;

                switch (this.Data)
                {
                    case List<long> v when v.Count > 0:
                        average = v.Average();
                        break;

                    case List<int> x when x.Count > 0:
                        average = x.Average();
                        break;

                    case List<float> y when y.Count > 0:
                        average = Convert.ToDouble(y.Average());
                        break;

                    case List<double> z when z.Count > 0:
                        average = z.Average();
                        break;
                }

                return average;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether there is an active warning state on this instance.
        /// Set to false when warning state changes to Ok.
        /// </summary>
        public bool ActiveErrorOrWarning
        {
            get => this.isInWarningState;

            set
            {
                this.isInWarningState = value;

                if (value)
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

            return this.AverageDataValue >= Convert.ToDouble(threshold);
        }

        public T StandardDeviation =>
            Data?.Count > 0 ? (T)Convert.ChangeType(Statistics.StandardDeviation(Data), typeof(T)) : default(T);

        public List<T> SlidingWindow =>
            Data?.Count > 0 ? Statistics.SlidingWindow(
                Data,
                9,
                WindowType.Max) : new List<T> { (T)Convert.ChangeType(-1, typeof(T)) };
    }
}
