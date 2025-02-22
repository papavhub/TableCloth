﻿using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using TableCloth.Resources;

namespace TableCloth.Components
{
    public sealed class AppStartup : IDisposable
    {
        public AppStartup(
            SharedLocations sharedLocations,
            ILogger<AppStartup> logger)
        {
            _sharedLocations = sharedLocations;
            _logger = logger;

            _mutex = new Mutex(true, $"Global\\{GetType().FullName}", out this._isFirstInstance);
        }

        ~AppStartup() => Dispose(false);

        public void Dispose() => Dispose(true);

        private void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                if (_mutex != null)
                {
                    _mutex.ReleaseMutex();
                    _mutex.Dispose();
                }
            }

            _disposed = true;
        }

        private bool _disposed;
        private readonly SharedLocations _sharedLocations;
        private readonly ILogger<AppStartup> _logger;

        private readonly Mutex _mutex;
        private readonly bool _isFirstInstance;

        public bool HasRequirementsMet(List<string> warnings, out Exception failedResaon, out bool isCritical)
        {
            if (!File.Exists(_sharedLocations.HostessZipFilePath))
            {
                failedResaon = new FileNotFoundException(StringResources.Error_Hostess_Missing);
                isCritical = true;
                return false;
            }

            if (!File.Exists(_sharedLocations.ImagesZipFilePath))
            {
                failedResaon = new FileNotFoundException(StringResources.Error_Images_Missing);
                isCritical = true;
                return false;
            }

            // https://stackoverflow.com/questions/336633/how-to-detect-windows-64-bit-platform-with-net
            var isWow64 = Environment.OSVersion.Version.Major >= 10
                && NativeMethods.IsWow64Process(Process.GetCurrentProcess().Handle, out var retVal)
                && retVal;
            var is64BitOperatingSystem = (IntPtr.Size == 8) || isWow64;

            if (!is64BitOperatingSystem)
            {
                failedResaon = new PlatformNotSupportedException(StringResources.Error_Windows_OS_Too_Old);
                isCritical = true;
                return false;
            }

            var wsbExecPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsSandbox.exe");

#if !DEBUG
            if (!File.Exists(wsbExecPath))
            {
                failedResaon = new PlatformNotSupportedException(StringResources.Error_Windows_Sandbox_Missing);
                isCritical = true;
                return false;
            }
#endif

            if (!this._isFirstInstance)
            {
                failedResaon = new ApplicationException(StringResources.Error_Already_TableCloth_Running);
                isCritical = true;
                return false;
            }

            var iePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Internet Explorer", "iexplore.exe");

            if (!File.Exists(iePath))
            {
                warnings.Add(StringResources.Error_IEMode_NotAvailable);
            }

            var osvi = new NativeMethods.OSVERSIONINFOEXW()
            {
                dwOSVersionInfoSize = Marshal.SizeOf(typeof(NativeMethods.OSVERSIONINFOEXW)),
            };

            var supportedOSEditions = new NativeMethods.OSEdition[]
            {
                NativeMethods.OSEdition.PRODUCT_EDUCATION,
                NativeMethods.OSEdition.PRODUCT_EDUCATION_N,
                NativeMethods.OSEdition.PRODUCT_ENTERPRISE,
                NativeMethods.OSEdition.PRODUCT_ENTERPRISE_E,
                NativeMethods.OSEdition.PRODUCT_ENTERPRISE_EVALUATION,
                NativeMethods.OSEdition.PRODUCT_ENTERPRISE_N,
                NativeMethods.OSEdition.PRODUCT_ENTERPRISE_N_EVALUATION,
                NativeMethods.OSEdition.PRODUCT_PRO_WORKSTATION,
                NativeMethods.OSEdition.PRODUCT_PRO_WORKSTATION_N,
                NativeMethods.OSEdition.PRODUCT_PROFESSIONAL,
                NativeMethods.OSEdition.PRODUCT_PROFESSIONAL_N,
            };

            if (!NativeMethods.GetVersionExW(ref osvi))
                warnings.Add(StringResources.Error_Cannot_Invoke_GetVersionEx(Marshal.GetLastWin32Error()));
            else
            {
                if (!NativeMethods.GetProductInfo(
                    osvi.dwMajorVersion, osvi.dwMinorVersion,
                    osvi.wServicePackMajor, osvi.wServicePackMinor,
                    out NativeMethods.OSEdition productType))
                    warnings.Add(StringResources.Error_Cannot_Invoke_GetProductInfo);
                else
                {
                    if (Array.IndexOf(supportedOSEditions, productType) < 0)
                        warnings.Add(StringResources.Error_SandboxMightNotAvailable);
                }
            }

            failedResaon = null;
            isCritical = false;
            return true;
        }

        public bool Initialize(out Exception failedReason, out bool isCritical)
        {
            var targetPath = _sharedLocations.AppDataDirectoryPath;

            if (!Directory.Exists(targetPath))
            {
                try { Directory.CreateDirectory(targetPath); }
                catch (Exception e)
                {
                    failedReason = new ApplicationException(StringResources.Error_Cannot_Create_AppDataDirectory(e), e);
                    isCritical = true;
                    return false;
                }
            }

            var imageDirectoryPath = _sharedLocations.GetImageDirectoryPath();

            if (!Directory.Exists(imageDirectoryPath))
            {
                try { Directory.CreateDirectory(imageDirectoryPath); }
                catch (Exception e)
                {
                    failedReason = new ApplicationException(StringResources.Error_Cannot_Create_AppDataDirectory(e), e);
                    isCritical = true;
                    return false;
                }
            }

            try
            {
                using (var imagesZipStream = File.OpenRead(_sharedLocations.ImagesZipFilePath))
                {
                    using var zipArchive = new ZipArchive(imagesZipStream, ZipArchiveMode.Read);

                    foreach (var eachEntry in zipArchive.Entries)
                    {
                        var destPath = Path.Combine(imageDirectoryPath, eachEntry.Name);

                        try
                        {
                            using (var outputStream = File.OpenWrite(destPath))
                            using (var eachStream = eachEntry.Open())
                            {
                                eachStream.CopyTo(outputStream);
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.LogWarning(e, $"Cannot write image file {destPath}.");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                failedReason = new ApplicationException(StringResources.Error_Cannot_Prepare_AppContents(e), e);
                isCritical = true;
                return false;
            }

            failedReason = null;
            isCritical = false;
            return true;
        }
    }
}
