// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserver.Observers.Utilities
{
    /// <summary>
    /// How to store CSV files produced by ObserverBase.CsvLogger instances.
    /// In either case, SingleFileWithArchives or MultipleFilesNoArchives, the lifetime of files on disk can be controlled by setting the MaxArchivedCsvFileLifetimeDays in Settings.xml.
    /// </summary>
    public enum CsvFileWriteFormat 
    {
        /// <summary>
        /// Store Csv data into single, long-running files (updated per observer run, per monitored entity) with archival after 1 day.
        /// </summary>
        SingleFileWithArchives,
        /// <summary>
        /// Store one Csv file per run per target monitored entity, time-stamped (Utc) with no archival (there is no need for archival).
        /// </summary>
        MultipleFilesNoArchives
    }
}
