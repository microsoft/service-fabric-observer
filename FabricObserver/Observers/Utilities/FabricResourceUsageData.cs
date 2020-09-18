// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Health;
using System.Linq;

namespace FabricObserver.Observers.Utilities
{
    // Why the generic constraint on struct? Because this type only works on numeric types,
    // which are all structs in .NET so it's really a partial constraint, but useful just the same.
    public class FabricResourceUsageData<T>
            where T : struct
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FabricResourceUsageData{T}"/> class.
        /// </summary>
        /// <param name="property">Metric string.</param>
        /// <param name="id">Instance id.</param>
        /// <param name="dataCapacity">Max element capacity of instance's data container.</param>
        /// <param name="useCircularBuffer">Whether to hold data in a Circular Buffer or not.</param>
        public FabricResourceUsageData(
            string property,
            string id,
            int dataCapacity = 30,
            bool useCircularBuffer = false)
        {
            if (string.IsNullOrEmpty(property))
            {
                throw new ArgumentException($"Must provide a non-empty {property}.");
            }

            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException($"Must provide a non-empty {id}.");
            }

            // This can be either a straight List<T> or a CircularBufferCollection<T>.
            if (useCircularBuffer)
            {
                if (dataCapacity <= 0)
                {
                    var healthReporter = new ObserverHealthReporter(new Logger("FabricResourceUsageData"));
                    var healthReport = new HealthReport
                    {
                        AppName = new Uri("fabric:/FabricObserver"),
                        HealthMessage = $"Incorrect CircularBuffer data capacity specified for FRUD instance: {dataCapacity}. " +
                        "Using default value 30. Please use a number greater than 0 for ResourceUsageDataCapacity setting in Settings.xml.",
                        State = HealthState.Ok,
                        ReportType = HealthReportType.Application,
                        NodeName = FabricRuntime.GetNodeContext()?.NodeName,
                        HealthReportTimeToLive = TimeSpan.MaxValue,
                        Observer = ObserverConstants.FabricObserverName,
                    };

                    healthReporter.ReportHealthToServiceFabric(healthReport);
                }

                Data = new CircularBufferCollection<T>(dataCapacity > 0 ? dataCapacity : 30);
            }
            else
            {
                Data = new List<T>();
            }

            Property = property;
            Id = id;
            Units = string.Empty;

            if (property.Contains("MB"))
            {
                Units = "MB";
            }

            if (property.ToLower().Contains("%") ||
                property.ToLower().Contains("cpu") ||
                property.ToLower().Contains("percent"))
            {
                Units = "%";
            }
        }

        /// <summary>
        /// Gets the name of the machine resource property this instance represents.
        /// </summary>
        public string Property { get; }

        /// <summary>
        /// Gets the unique Id of this instance.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Gets the unit of measure for the data (%, MB/GB, etc).
        /// </summary>
        public string Units { get; }

        /// <summary>
        /// Gets IList type that holds the resource monitoring
        /// numeric data.
        /// </summary>
        public IList<T> Data { get; }

        private bool isInWarningState;
        private string foErrorCode;

        /// <summary>
        /// Gets count of warnings per observer instance across iterations for the lifetime of the Observer.
        /// </summary>
        public int LifetimeWarningCount { get; private set; }

        /// <summary>
        /// Gets the largest numeric value in the Data collection.
        /// </summary>
        public T MaxDataValue => Data?.Count > 0 ? Data.Max() : default(T);

        /// <summary>
        /// Gets the average value in the Data collection.
        /// </summary>
        public T AverageDataValue
        {
            get
            {
                T average = default(T);

                switch (Data)
                {
                    case IList<long> v when v.Count > 0:
                        average = (T)Convert.ChangeType(Math.Round(v.Average(), 1), typeof(T));
                        break;

                    case IList<int> x when x.Count > 0:
                        average = (T)Convert.ChangeType(Math.Round(x.Average(), 1), typeof(T));
                        break;

                    case IList<float> y when y.Count > 0:
                        average = (T)Convert.ChangeType(Math.Round(y.Average(), 1), typeof(T));
                        break;

                    case IList<double> z when z.Count > 0:
                        average = (T)Convert.ChangeType(Math.Round(z.Average(), 1), typeof(T));
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
                    LifetimeWarningCount++;
                }
            }
        }

        /// <summary>
        /// Gets or sets the active error or warning code (FOErorrWarningCode).
        /// </summary>
        public string ActiveErrorOrWarningCode
        {
            get => this.foErrorCode;

            set
            {
                this.foErrorCode = value;
            }
        }

        /// <summary>
        /// Determines whether or not a supplied threshold has been reached when taking the average of the values in the Data collection.
        /// </summary>
        /// <typeparam name="TU">Error/Warning Threshold value to determine health state decision.</typeparam>
        /// <param name="threshold">Threshold value to measure against.</param>
        /// <returns>Returns true or false depending upon computed health state based on supplied threshold value.</returns>
        public bool IsUnhealthy<TU>(TU threshold)
        {
            if (Data.Count < 1 || Convert.ToDouble(threshold) < 1)
            {
                return false;
            }

            return Convert.ToDouble(AverageDataValue) >= Convert.ToDouble(threshold);
        }

        /// <summary>
        /// Gets the standard deviation of the data held in the Data collection.
        /// </summary>
        public T StandardDeviation =>
            Data?.Count > 0 ? Statistics.StandardDeviation(Data) : default;

        /// <summary>
        /// Gets SlidingWindow Max: A sorted list of sliding window maximums.
        /// </summary>
        public IList<T> SlidingWindowMax =>
            Data?.Count > 0 ? Statistics.SlidingWindow(
                Data,
                3,
                WindowType.Max) : new List<T> { (T)Convert.ChangeType(-1, typeof(T)) };

        /// <summary>
        ///  Gets SlidingWindow Min: A sorted list of sliding window minimums.
        /// </summary>
        public IList<T> SlidingWindowMin =>
            Data?.Count > 0 ? Statistics.SlidingWindow(
                Data,
                3,
                WindowType.Min) : new List<T> { (T)Convert.ChangeType(-1, typeof(T)) };
    }
}
