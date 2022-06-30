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
    // Why the generic constraint on struct? Because this type only works on numeric types,
    // which are all structs in .NET so it's really a partial constraint, but useful just the same.
    public class FabricResourceUsageData<T> where T : struct
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
                     int dataCapacity,
                     bool useCircularBuffer = false,
                     bool isParallel = false)
        {
            if (string.IsNullOrEmpty(property))
            {
                throw new ArgumentException($"Must provide a non-empty {property}.");
            }

            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException($"Must provide a non-empty {id}.");
            }

            // Data can be a List<T>, a CircularBufferCollection<T>, or a ConcurrentQueue<T>. \\

            // CircularBufferCollection is not thread safe for writes. 
            if (useCircularBuffer && !isParallel)
            {
                Data = new CircularBufferCollection<T>(dataCapacity > 0 ? dataCapacity : 3);
            }
            else if (isParallel)
            {
                Data = new ConcurrentQueue<T>();
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

            if (property.Contains("%") ||
                property.ToLower().Contains("cpu") ||
                property.ToLower().Contains("percent"))
            {
                Units = "%";
            }
        }

        /// <summary>
        /// Gets or sets the name of the machine resource property this instance represents.
        /// </summary>
        public string Property
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the unique Id of this instance.
        /// </summary>
        public string Id
        {
            get; set;
        }

        /// <summary>
        /// Gets or sets the unit of measure for the data (%, MB/GB, etc).
        /// </summary>
        public string Units
        {
            get; set;
        }

        /// <summary>
        /// Gets the IEnumerable type that holds the resource monitoring numeric data.
        /// These can be one of generic List, CircularBufferCollection or ConcurrentQueue types.
        /// </summary>
        public IEnumerable<T> Data
        {
            get; set;
        }

        private bool isInWarningState;

        /// <summary>
        /// Gets count of total warnings for the lifetime of this instance.
        /// </summary>
        public int LifetimeWarningCount
        {
            get; private set;
        }

        /// <summary>
        /// Gets the largest numeric value in the Data collection.
        /// </summary>
        public T MaxDataValue
        {
            get
            {
                if (Data?.Count() > 0)
                {
                    return Data.Max();
                }

                return default;
            }
        }

        /// <summary>
        /// Gets the average value in the Data collection.
        /// </summary>
        public double AverageDataValue
        {
            get
            {
                double average = 0.0;

                if (Data == null || Data.Count() == 0)
                {
                    return average;
                }

                switch (Data)
                {
                    // Thread safe for reads only: List<T>, CircularBufferCollection<T> \\

                    case IList<long> v:
                        average = Math.Round(v.Average(), 2);
                        break;

                    case IList<int> x:
                        average = Math.Round(x.Average(), 2);
                        break;

                    case IList<float> y:
                        average = Math.Round(y.Average(), 2);
                        break;

                    case IList<double> z:
                        average = Math.Round(z.Average(), 2);
                        break;


                    // Thread safe for reads and writes: ConcurrentQueue<T> \\

                    case IProducerConsumerCollection<long> v:
                        average = Math.Round(v.Average(), 2);
                        break;

                    case IProducerConsumerCollection<int> x:
                        average = Math.Round(x.Average(), 2);
                        break;

                    case IProducerConsumerCollection<float> y:
                        average = Math.Round(y.Average(), 2);
                        break;

                    case IProducerConsumerCollection<double> z:
                        average = Math.Round(z.Average(), 2);
                        break;
                }
                
                return average;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether there is an active Error or Warning health state on this instance.
        /// Set to false when health state changes to Ok.
        /// </summary>
        public bool ActiveErrorOrWarning
        {
            get => isInWarningState;

            set
            {
                isInWarningState = value;

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
            get; set; 
        }

        /// <summary>
        /// Determines whether or not a supplied threshold has been reached when taking the average of the values in the Data collection.
        /// </summary>
        /// <typeparam name="TU">Error/Warning numeric (thus struct) threshold value to determine health state.</typeparam>
        /// <param name="threshold">Numeric threshold value to measure against.</param>
        /// <returns>Returns true or false depending upon computed health state based on supplied threshold value.</returns>
        public bool IsUnhealthy<TU>(TU threshold) where TU : struct
        {
            if (Data == null || !Data.Any() || Convert.ToDouble(threshold) <= 0.0)
            {
                return false;
            }

            return AverageDataValue >= Convert.ToDouble(threshold);
        }

        /// <summary>
        /// Gets the standard deviation of the data held in the Data collection.
        /// </summary>
        public T StandardDeviation => Data?.Count() > 0 ? Statistics.StandardDeviation(Data) : default;

        /// <summary>
        /// Gets SlidingWindow Max: A sorted list of sliding window maximums. This is only availabe when Data is CircularBufferCollection.
        /// </summary>
        public IList<T> SlidingWindowMax => Data is CircularBufferCollection<T> && Data?.Count() >= 3 ? Statistics.SlidingWindow(Data, 3, WindowType.Max) : null;

        /// <summary>
        ///  Gets SlidingWindow Min: A sorted list of sliding window minimums. This is only availabe when Data is CircularBufferCollection.
        /// </summary>
        public IList<T> SlidingWindowMin => Data is CircularBufferCollection<T> && Data ?.Count() >= 3 ? Statistics.SlidingWindow(Data, 3, WindowType.Min) : null;

        /// <summary>
        /// Adds numeric data to current instance's Data property. Use this method versus adding directly to Data.
        /// </summary>
        /// <param name="data"></param>
        public void AddData(T data)
        {
            if (Data is List<T> d)
            {
                d.Add(data);
            }
            else if (Data is CircularBufferCollection<T> c)
            {
                c.Add(data);
            }
            else if (Data is ConcurrentQueue<T> e)
            {
                e.Enqueue(data);
            }
        }

        /// <summary>
        /// Clears numeric data of the current instance's Data property. Use this method versus calling Clear on Data as that
        /// won't work for ConcurrentQueue.
        /// </summary>
        public void ClearData()
        {
            if (Data is List<T> d)
            {
                d.TrimExcess();
                d.Clear();
            }
            else if (Data is CircularBufferCollection<T> c)
            {
                c.Clear();
            }
            else if (Data is ConcurrentQueue<T>)
            {
                // .NET Standard 2.0 does not have a Clear() (2.1 does).
                Data = new ConcurrentQueue<T>();
            }
        }
    }
}