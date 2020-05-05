// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using Microsoft.Win32;

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// Utility which helps in getting and settings the Windows Update settings of a Windows based system
    /// </summary>
    public class WindowsAutoUpdateUtility
    {
        /// <summary>
        /// const reg key path.
        /// </summary>
        public const string AURegPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";

        /// <summary>
        /// Initializes a new instance of the <see cref="WindowsAutoUpdateUtility"/> class.
        /// ctor.
        /// </summary>
        public WindowsAutoUpdateUtility()
        {
            RegistryKey auKey = Registry.LocalMachine.OpenSubKey(AURegPath, true);

            if (auKey != null)
            {
                this.IsAutoUpdateDownloadEnabled =
                    !(Convert.ToInt32(auKey.GetValue("NoAutoUpdate")) == 0
                    && Convert.ToInt32(auKey.GetValue("AUOptions")) == 2);
            }
        }

        /// <summary>
        /// Gets a value indicating whether automatic update is enabled.
        /// </summary>
        public bool IsAutoUpdateDownloadEnabled
        {
            get;
        }
    }
}
