// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;

namespace FabricObserver.Observers.Utilities
{
    // https://stackoverflow.com/questions/1563191/cleanest-way-to-write-retry-logic
    public static class Retry
    {
        public static void Do(Action action, TimeSpan retryInterval, CancellationToken token, int maxAttempts = 3)
        {
            _ = Do<object>(
                () =>
                {
                    token.ThrowIfCancellationRequested();
                    action();
                    return null;
                }, 
                retryInterval,
                token,
                maxAttempts);
        }

        public static T Do<T>(Func<T> action, TimeSpan retryInterval, CancellationToken token, int maxAttemptCount = 3)
        {
            var exceptions = new List<Exception>();

            for (int attempted = 0; attempted < maxAttemptCount; attempted++)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    if (attempted > 0)
                    {
                        Thread.Sleep(retryInterval);
                    }

                    return action();
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }

            throw new AggregateException(exceptions);
        }
    }
}
