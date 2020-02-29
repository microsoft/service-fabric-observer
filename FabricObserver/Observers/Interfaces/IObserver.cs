// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Interfaces
{
    public interface IObserver : IDisposable
    {
        DateTime LastRunDateTime { get; set; }

        TimeSpan RunInterval { get; set; }

        bool IsEnabled { get; set; }

        bool HasActiveFabricErrorOrWarning { get; set; }

        bool IsUnhealthy { get; set; }

        /// <summary>
        /// The function where observers observe.
        /// </summary>
        /// <param name="token">Cancellation token used to stop observers.</param>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        Task ObserveAsync(CancellationToken token);

        /// <summary>
        /// The function where observes report.
        /// </summary>
        /// <param name="token">Cancellation token used to stop observers.</param>
        /// <returns>A <see cref="Task"/> representing the result of the asynchronous operation.</returns>
        Task ReportAsync(CancellationToken token);
    }
}