// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Diagnostics;
using System.Fabric;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// Helper class to execute fabric client operations with retry.
    /// </summary>
    public static class FabricClientRetryHelper
    {
        private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(2);
        private static readonly Logger Logger = new Logger("FabricClientRetryHelper");

        /// <summary>
        /// Helper method to execute given function with defaultFabricClientRetryErrors and default Operation Timeout.
        /// </summary>
        /// <param name="function">Action to be performed.</param>
        /// <param name="cancellationToken">Cancellation token for Async operation.</param>
        /// <returns>Task object.</returns>
        public static async Task<T> ExecuteFabricActionWithRetryAsync<T>(Func<Task<T>> function, CancellationToken cancellationToken)
        {
            return await ExecuteFabricActionWithRetryAsync(
                          function,
                          new FabricClientRetryErrors(),
                          DefaultOperationTimeout,
                          cancellationToken);
        }

        /// <summary>
        /// Helper method to execute given function with given user FabricClientRetryErrors and given Operation Timeout.
        /// </summary>
        /// <param name="function">Action to be performed.</param>
        /// <param name="errors">Fabric Client Errors that can be retired.</param>
        /// <param name="operationTimeout">Timeout for the operation.</param>
        /// <param name="cancellationToken">Cancellation token for Async operation.</param>
        /// <returns>Task object.</returns>
        public static async Task<T> ExecuteFabricActionWithRetryAsync<T>(
                                        Func<Task<T>> function,
                                        FabricClientRetryErrors errors,
                                        TimeSpan operationTimeout,
                                        CancellationToken cancellationToken)
        {
            bool needToWait = false;
            Stopwatch watch = Stopwatch.StartNew();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (needToWait)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }

                try
                {
                    return await function();
                }
                catch (Exception e)
                {
                    if (!HandleException(e, errors, out bool retryElseSuccess))
                    {
                        throw;
                    }

                    if (retryElseSuccess)
                    {
                        Logger.LogInfo($"ExecuteFabricActionWithRetryAsync: Retrying due to Exception: {e}");

                        if (watch.Elapsed > operationTimeout)
                        {
                            Logger.LogWarning(
                                    "ExecuteFabricActionWithRetryAsync: Done Retrying. " +
                                    $"Time Elapsed: {watch.Elapsed.TotalSeconds}, " +
                                    $"Timeout: {operationTimeout.TotalSeconds}. " +
                                    $"Throwing Exception: {e}");

                            throw;
                        }

                        needToWait = true;

                        continue;
                    }

                    Logger.LogInfo($"ExecuteFabricActionWithRetryAsync: Exception {e} Handled but No Retry.");

                    return default;
                }
            }
        }

        private static bool HandleException(Exception e, FabricClientRetryErrors errors, out bool retryElseSuccess)
        {
            var fabricException = e as FabricException;

            if (errors.RetryableExceptions.Contains(e.GetType()))
            {
                retryElseSuccess = true /*retry*/;
                return true;
            }

            if (fabricException != null && errors.RetryableFabricErrorCodes.Contains(fabricException.ErrorCode))
            {
                retryElseSuccess = true /*retry*/;
                return true;
            }

            if (errors.RetrySuccessExceptions.Contains(e.GetType()))
            {
                retryElseSuccess = false /*success*/;
                return true;
            }

            if (fabricException != null
                && errors.RetrySuccessFabricErrorCodes.Contains(fabricException.ErrorCode))
            {
                retryElseSuccess = false /*success*/;
                return true;
            }

            if (e.GetType() == typeof(FabricTransientException))
            {
                retryElseSuccess = true /*retry*/;
                return true;
            }

            if (fabricException?.InnerException != null)
            {
                if (fabricException.InnerException is COMException ex && errors.InternalRetrySuccessFabricErrorCodes.Contains((uint)ex.ErrorCode))
                {
                    retryElseSuccess = false /*success*/;
                    return true;
                }
            }

            retryElseSuccess = false;
            return false;
        }
    }
}
