// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Interfaces
{
    public interface IObserver : IDisposable
    {
        DateTime LastRunDateTime { get; set; }
        TimeSpan RunInterval { get; set; }
        bool IsEnabled { get; set; }
        bool HasActiveFabricErrorOrWarning { get; set; }
        bool IsUnhealthy { get; set; }
        Task ObserveAsync(CancellationToken token);
        Task ReportAsync(CancellationToken token);
    }
}