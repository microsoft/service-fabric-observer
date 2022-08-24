// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Runtime.Serialization;

namespace FabricObserverTests
{
    /// <summary>
    /// Internal exception that is thrown when a custom generated health report is not found in the health event store.
    /// </summary>
    [Serializable]
    internal class HealthReportNotFoundException : Exception
    {
        /// <summary>
        /// Creates an instance of HealthReportNotFoundException.
        /// </summary>
        public HealthReportNotFoundException()
        {
        }

        /// <summary>
        ///  Creates an instance of HealthReportNotFoundException.
        /// </summary>
        /// <param name="message">Error message that describes the problem.</param>
        public HealthReportNotFoundException(string message) : base(message)
        {
        }

        /// <summary>
        /// Creates an instance of HealthReportNotFoundException.
        /// </summary>
        /// <param name="message">Error message that describes the problem.</param>
        /// <param name="innerException">InnerException instance.</param>
        public HealthReportNotFoundException(string message, Exception innerException) : base(message, innerException)
        {
        }

        /// <summary>
        /// Creates an instance of HealthReportNotFoundException.
        /// </summary>
        /// <param name="info">SerializationInfo</param>
        /// <param name="context">StreamingContext</param>
        protected HealthReportNotFoundException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}