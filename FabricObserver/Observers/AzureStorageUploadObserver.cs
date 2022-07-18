// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using FabricObserver.Observers.Utilities;
using System;
using System.Diagnostics;
using System.Fabric;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace FabricObserver.Observers
{
    // AzureStorageObserver is an observer that periodically checks for the existence of dmp files in observer_logs folder. It then uploads them if it has been
    // configured to do so - assuming correctly encrypted and specified ConnectionString for an Azure Storage Account, a container name, and other basic settings.
    // Since only Windows is supported for dumping service processes today by FO, this observer is not useful for Liunx in this version. 
    // So, if you are deploying FO to Linux servers, then don't enable this observer (it won't do anything if it is enabled, so no need have it resident in memory).
    public sealed class AzureStorageUploadObserver : ObserverBase
    {
        private readonly Stopwatch stopwatch;

        // Only AppObserver is supported today. No other observers generate dmp files.
        private const string AppObserverDumpFolder = "MemoryDumps";
        private string appObsDumpFolderPath;

        private SecureString StorageConnectionString
        {
            get; set;
        }

        private string BlobContainerName
        {
            get; set;
        }

        private AuthenticationType AuthenticationType
        {
            get; set;
        }

        private string StorageAccountName
        {
            get; set;
        }

        private SecureString StorageAccountKey
        {
            get; set;
        }

        private CompressionLevel ZipCompressionLevel
        {
            get; set;
        } = CompressionLevel.Optimal;

        /// <summary>
        /// Creates a new instance of the type.
        /// </summary>
        /// <param name="context">The StatelessServiceContext instance.</param>
        public AzureStorageUploadObserver(StatelessServiceContext context) : base(null, context)
        {
            stopwatch = new Stopwatch();
        }

        public override async Task ObserveAsync(CancellationToken token)
        {
            // Since there is currently only support for Windows process dumps (by AppObserver only), there is no need to run this Observer on Linux (today..).
            // The dumps created are *not* crash dumps, they are live dumps of a process's memory, handles, threads, stack.. So, the target process will not be killed.
            // By default, the dmp files are MiniPlus, so they will roughly be as large as the process's private working set. You can set to Mini (similar size) or 
            // Full, much larger. You probably do not need to create Full dumps in most cases.
            if (!IsWindows)
            {
                return;
            }

            // If set, this observer will only run during the supplied interval.
            if (RunInterval > TimeSpan.MinValue && DateTime.Now.Subtract(LastRunDateTime) < RunInterval)
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            Token = token;
            stopwatch.Start();

            if (!Initialize())
            {
                stopwatch.Stop();
                stopwatch.Reset();
                LastRunDateTime = DateTime.Now;
                return;
            }

            // In case upload failed, also try and upload any zip files that remained in target local directory.
            await ProcessFilesAsync(appObsDumpFolderPath, new[] { "*.zip", "*.dmp" }, token);
            await ReportAsync(token);

            CleanUp();

            // The time it took to run this observer.
            stopwatch.Stop();
            RunDuration = stopwatch.Elapsed;

            if (EnableVerboseLogging)
            {
                ObserverLogger.LogInfo($"Run Duration: {RunDuration}");
            }

            stopwatch.Reset();
            LastRunDateTime = DateTime.Now;
        }

        private void CleanUp()
        {
            if (StorageConnectionString != null)
            {
                StorageConnectionString.Dispose();
                StorageConnectionString = null;
            }

            if (StorageAccountKey != null)
            {
                StorageAccountKey?.Dispose();
                StorageAccountKey = null;
            }
        }

        private async Task ProcessFilesAsync(string folderPath, string[] searchPatterns, CancellationToken token)
        {
            for (int i = 0; i < searchPatterns.Length; ++i)
            {
                token.ThrowIfCancellationRequested();

                string searchPattern = searchPatterns[i];
                string[] files = Directory.GetFiles(folderPath, searchPattern, SearchOption.AllDirectories);

                for (int j = 0; j < files.Length; ++j)
                {
                    token.ThrowIfCancellationRequested();
                    
                    string file = files[j];

                    if (searchPattern == "*.dmp")
                    {
                        if (!CompressFileForUpload(file))
                        {
                            continue;
                        }
                    }

                    string zipfile = file.Replace(".dmp", ".zip");
                    bool success = await UploadBlobAsync(zipfile, token);

                    await Task.Delay(1000, token);

                    if (!success)
                    {
                        continue;
                    }

                    try
                    {
                        Retry.Do(() => File.Delete(zipfile), TimeSpan.FromSeconds(1), token, 3);
                    }
                    catch (AggregateException ae)
                    {
                        ObserverLogger.LogWarning($"Unable to delete file {Path.GetFileName(zipfile)} after successful upload " +
                                                  $"to blob container {BlobContainerName}:{Environment.NewLine}{ae}");
                    }
                }
            }
        }

        private bool CompressFileForUpload(string file, bool deleteOriginal = true)
        {
            if (!File.Exists(file))
            {
                return false;
            }

            if (file.EndsWith(".zip"))
            {
                return true;
            }

            string zipPath = null;

            try
            {
                zipPath = file.Replace(".dmp", ".zip");
                using FileStream fs = new FileStream(zipPath, FileMode.Create);
                using ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create);
                zip.CreateEntryFromFile(file, Path.GetFileName(file), ZipCompressionLevel);
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is NotSupportedException || e is UnauthorizedAccessException)
            {
                ObserverLogger.LogWarning($"Unable to compress file for uploading:{Environment.NewLine}{e}");
                return false;
            }

            // Delete the original file if compression succeeds.
            if (deleteOriginal && File.Exists(zipPath))
            {
                try
                {
                    Retry.Do(() => File.Delete(file), TimeSpan.FromSeconds(1), Token, 3);
                }
                catch (AggregateException ae)
                {
                    ObserverLogger.LogWarning($"Unable to delete original file after successful compression to zip file:{Environment.NewLine}{ae}");
                }
            }

            return true;
        }

        public override Task ReportAsync(CancellationToken token)
        {
            // This observer does not report.
            return Task.FromResult(0);
        }

        private bool Initialize()
        {
            appObsDumpFolderPath = Path.Combine(ObserverLogger.LogFolderBasePath, ObserverConstants.AppObserverName, AppObserverDumpFolder);

            // Nothing to do here.
            if (!Directory.Exists(appObsDumpFolderPath))
            {
                return false;
            }

            try
            {
                var files = Directory.GetFiles(appObsDumpFolderPath, "*.dmp", SearchOption.AllDirectories);

                if (files.Length == 0)
                {
                    files = Directory.GetFiles(appObsDumpFolderPath, "*.zip", SearchOption.AllDirectories);

                    if (files.Length == 0)
                    {
                        return false;
                    }
                }
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException)
            {
                ObserverLogger.LogWarning("Initialize(): Unable to determine existence of dmp files in observer log directories. Aborting..");
                return false;
            }
            
            string connString = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.AzureStorageConnectionStringParameter);
            string accountName = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.AzureStorageAccountNameParameter);
            string accountKey = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.AzureStorageAccountKeyParameter); 

            if (string.IsNullOrWhiteSpace(connString) && string.IsNullOrWhiteSpace(accountName) && string.IsNullOrWhiteSpace(accountKey))
            {
                ObserverLogger.LogWarning("Initialize: No authentication information provided. Aborting..");
                return false;
            }

            BlobContainerName = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.AzureBlobContainerNameParameter);

            if (string.IsNullOrWhiteSpace(BlobContainerName))
            {
                ObserverLogger.LogWarning("Initialize: No container name provided. Aborting..");
                return false;
            }

            // Clean out old dmp files, if any. Generally, there will only be some dmp files remaining on disk if customer has not configured
            // uploads correctly or some error during some stage of the upload process. Under normal circumstances, there will be no dmp (or zip) files remaining on
            // disk after successful uploads to configured blob storage container.
            ObserverLogger.TryCleanFolder(appObsDumpFolderPath, "*.dmp", TimeSpan.FromDays(3));

            // Compression setting.
            string compressionLevel = GetSettingParameterValue(ConfigurationSectionName, ObserverConstants.ZipFileCompressionLevelParameter);

            if (Enum.TryParse(compressionLevel, true, out CompressionLevel compressLevel))
            {
                ZipCompressionLevel = compressLevel;
            }

            // Decrypt connection string.\\

            if (!string.IsNullOrWhiteSpace(connString))
            {
                AuthenticationType = AuthenticationType.ConnectionString;
                bool isEncrypted = ConfigPackage.Settings.Sections[ConfigurationSectionName].Parameters[ObserverConstants.AzureStorageConnectionStringParameter].IsEncrypted;

                if (isEncrypted)
                {
                    try
                    {
                        StorageConnectionString = ConfigPackage.Settings.Sections[ConfigurationSectionName].Parameters[ObserverConstants.AzureStorageConnectionStringParameter].DecryptValue();
                    }
                    catch (Exception e)
                    {
                        ObserverLogger.LogWarning($"Unable to decrypt Azure Storage Connection String:{Environment.NewLine}{e}");
                        return false;
                    }
                }
                else
                {
                    ObserverLogger.LogInfo("You have not encrypted your Azure Storage ConnectionString. This is not safe. " +
                                           "Please encrypt it using the Invoke-ServiceFabricEncryptText PowerShell cmdlet.");

                    // TOTHINK: Don't enable non-encrypted connection string support. Just return false here? 
                    char[] cArr = connString.ToCharArray();
                    StorageConnectionString = SecureStringFromCharArray(cArr, 0, cArr.Length);

                    // SecureStringFromCharArray returns null if it fails to convert. It logs failure reason to the local log directory of this observer.
                    if (StorageConnectionString == null)
                    {
                        return false;
                    }
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(accountKey))
                {
                    ObserverLogger.LogWarning("You have not provided required Azure Storage account information. Aborting...");
                    return false;
                }

                AuthenticationType = AuthenticationType.SharedKey;
                StorageAccountName = accountName;
                bool isEncrypted = ConfigPackage.Settings.Sections[ConfigurationSectionName].Parameters[ObserverConstants.AzureStorageAccountKeyParameter].IsEncrypted;

                if (isEncrypted)
                {
                    try
                    {
                        StorageAccountKey = ConfigPackage.Settings.Sections[ConfigurationSectionName].Parameters[ObserverConstants.AzureStorageAccountKeyParameter].DecryptValue();
                    }
                    catch (Exception e)
                    {
                        ObserverLogger.LogWarning($"Unable to decrypt Azure Storage Account Key:{Environment.NewLine}{e}");
                        return false; 
                    }
                }
                else
                {
                    ObserverLogger.LogInfo("You have not encrypted your Azure Storage Account Key. This is not safe. " +
                                           "Please encrypt it using the Invoke-ServiceFabricEncryptText PowerShell cmdlet.");

                    // TOTHINK: Don't enable non-encrypted connection string support. Just return false here? 
                    char[] cArr = accountKey.ToCharArray();
                    StorageAccountKey = SecureStringFromCharArray(cArr, 0, cArr.Length);

                    // SecureStringFromCharArray returns null if it fails to convert. It logs failure reason to the local log directory of this observer.
                    if (StorageAccountKey == null)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private async Task<bool> UploadBlobAsync(string filePath, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || token.IsCancellationRequested)
            {
                return false;
            }

            string blobName = Path.GetFileName(filePath);
            bool success = false;
            BlobContainerClient container = null;

            if (AuthenticationType == AuthenticationType.ConnectionString)
            {
                char[] arr = SecureStringToCharArray(StorageConnectionString);

                // SecureStringToCharArray returns null if it fails to convert. It logs failure reason to the local log directory of this observer.
                if (arr == null)
                {
                    return false;
                }

                string s = new string(arr);

                // Create a client that can authenticate with a connection string.
                container = new BlobContainerClient(s, BlobContainerName);
            }
            else
            {
                string accountName = StorageAccountName;
                char[] arr = SecureStringToCharArray(StorageAccountKey);

                // SecureStringToCharArray returns null if it fails to convert. It logs failure reason to the local log directory of this observer.
                if (arr == null)
                {
                    return false;
                }

                string accountKey = new string(arr);
                Uri serviceUri = new Uri($"https://{accountName}.blob.core.windows.net/{BlobContainerName}");

                // Create a SharedKeyCredential that we can use to authenticate.
                StorageSharedKeyCredential credential = new StorageSharedKeyCredential(accountName, accountKey);

                // Create a client that can authenticate with a shared key credential.
                container = new BlobContainerClient(serviceUri, credential);
            }

            _ = container.CreateIfNotExists();
            token.ThrowIfCancellationRequested();
            BlobClient blob = container.GetBlobClient(blobName);

            // Upload local zip file.

            await blob.UploadAsync(filePath, token).ContinueWith(
                (response) =>
                {
                    if (response.IsFaulted)
                    {
                        ObserverLogger.LogWarning($"Upload of blob {Path.GetFileName(filePath)} " +
                                                  $"failed:{Environment.NewLine}{response.Exception.Message}");

                        // Try and delete local duplicate blob file (it already exists in the blob storage account).
                        if (response.Exception.InnerException is RequestFailedException ex)
                        {
                            if (ex.ErrorCode == "BlobAlreadyExists" || ex.HResult == -2146233088)
                            {
                                ObserverLogger.LogWarning($"Deleting duplicate blob {blobName} from local disk..");
                                try
                                {
                                    Retry.Do(() => File.Delete(filePath), TimeSpan.FromSeconds(1), token);
                                    ObserverLogger.LogWarning($"Successfully deleted duplicate blob {blobName} from local disk..");
                                }
                                catch (AggregateException ae)
                                {
                                    ObserverLogger.LogWarning($"Can't delete local duplicate blob {blobName}:{Environment.NewLine}{ae}");
                                }
                            }
                        }
                        success = false;
                    }
                    else if (response.IsCompletedSuccessfully)
                    {
                        ObserverLogger.LogInfo($"Successfully uploaded file {Path.GetFileName(filePath)} " +
                                               $"to blob container {BlobContainerName}.");
                        success = true;
                    }

                }, token).ConfigureAwait(true);

            return success;
        }

        // SecureString helpers \\

        private SecureString SecureStringFromCharArray(char[] charArray, int start, int end)
        {
            SecureString secureString = new SecureString();

            try
            {
                for (int i = start; i < end; i++)
                {
                    Token.ThrowIfCancellationRequested();

                    secureString.AppendChar(charArray[i]);
                }
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                ObserverLogger.LogWarning($"Unable to create SecureString from supplied char array:{Environment.NewLine}{e}");
                return null;
            }

            secureString.MakeReadOnly();
            return secureString;
        }

        private char[] SecureStringToCharArray(SecureString secureString)
        {
            char[] charArray = new char[secureString.Length];
            IntPtr ptr = IntPtr.Zero;

            try
            {
                ptr = SecureStringMarshal.SecureStringToGlobalAllocUnicode(secureString);
                Marshal.Copy(ptr, charArray, 0, secureString.Length);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException)
            {
                ObserverLogger.LogWarning($"Can't convert SecureString instance to string:{Environment.NewLine}{e}");
                charArray = null;
            }
            finally
            {
                Marshal.ZeroFreeGlobalAllocUnicode(ptr);
            }

            return charArray;
        }
    }

    public enum AuthenticationType
    {
        ConnectionString,
        SharedKey
    }
}
