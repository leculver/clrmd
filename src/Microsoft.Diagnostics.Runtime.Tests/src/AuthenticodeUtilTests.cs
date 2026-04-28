// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

using System;
using System.IO;
using System.Linq;
using Microsoft.Diagnostics.Runtime.Utilities;
using Xunit;

namespace Microsoft.Diagnostics.Runtime.Tests
{
    public class AuthenticodeUtilTests
    {
        // Bit values for WinVerifyTrust dwProvFlags from wintrust.h.
        private const uint WTD_REVOCATION_CHECK_CHAIN = 0x00000040;
        private const uint WTD_REVOKE_WHOLECHAIN = 0x00000080;
        private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

        /// <summary>
        /// Regression test for the offline-only revocation finding (clrMD security review,
        /// Finding 1).  WinVerifyTrust must NOT be invoked with WTD_CACHE_ONLY_URL_RETRIEVAL.
        /// With that flag, a stale-but-still-within-NextUpdate cached CRL/OCSP "good"
        /// response would mask a freshly-revoked signing cert.  The constructed flags must
        /// also enable whole-chain revocation checking.
        /// </summary>
        [Fact]
        public void DwProvFlags_ExcludeCacheOnly_IncludeWholeChainRevocation()
        {
            uint flags = AuthenticodeUtil.DwProvFlags;

            Assert.Equal(0u, flags & WTD_CACHE_ONLY_URL_RETRIEVAL);
            Assert.Equal(WTD_REVOCATION_CHECK_CHAIN, flags & WTD_REVOCATION_CHECK_CHAIN);
            Assert.Equal(WTD_REVOKE_WHOLECHAIN, flags & WTD_REVOKE_WHOLECHAIN);
        }

        [Fact]
        public void VerifyDacDll_NonExistentPath_ThrowsFileNotFound()
        {
            string path = Path.Combine(Path.GetTempPath(), "definitely-does-not-exist-" + Guid.NewGuid().ToString("N") + ".dll");
            Assert.Throws<FileNotFoundException>(() => AuthenticodeUtil.VerifyDacDll(path, out _));
        }

        /// <summary>
        /// kernel32.dll is Microsoft-signed and chains to a Microsoft root, but it does not
        /// carry the DAC-specific EKU OID (1.3.6.1.4.1.311.84.4.1).  Verification must
        /// return false in that case so a non-DAC Microsoft binary cannot be substituted
        /// for the DAC.  The file lock should still be opened (to mirror the DAC path).
        /// </summary>
        [WindowsFact]
        public void VerifyDacDll_NonDacMicrosoftSignedBinary_ReturnsFalse()
        {
            string kernel32 = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
            Assert.True(File.Exists(kernel32), "kernel32.dll should exist on Windows test host.");

            bool ok = AuthenticodeUtil.VerifyDacDll(kernel32, out IDisposable? fileLock);
            try
            {
                Assert.False(ok, "kernel32.dll lacks the DAC EKU OID and must not verify as a DAC.");
            }
            finally
            {
                fileLock?.Dispose();
            }
        }

        /// <summary>
        /// Integration test: locate a real installed mscordaccore.dll and verify it passes
        /// the hardened (online-revocation) signature/policy/EKU pipeline.  Skipped when no
        /// installed runtime DAC is available (e.g. CI without a Microsoft.NETCore.App share).
        /// </summary>
        [WindowsFact]
        public void VerifyDacDll_RealInstalledDac_ReturnsTrue()
        {
            string? dacPath = TryFindInstalledDac();
            if (dacPath is null)
            {
                return; // no installed runtime DAC available on this host; nothing to assert.
            }

            bool ok = AuthenticodeUtil.VerifyDacDll(dacPath, out IDisposable? fileLock);
            try
            {
                Assert.True(ok, $"Expected installed DAC to verify: {dacPath}");
            }
            finally
            {
                fileLock?.Dispose();
            }
        }

        private static string? TryFindInstalledDac()
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string sharedRoot = Path.Combine(programFiles, "dotnet", "shared", "Microsoft.NETCore.App");
            if (!Directory.Exists(sharedRoot))
                return null;

            return Directory.EnumerateDirectories(sharedRoot)
                .OrderByDescending(d => d, StringComparer.Ordinal)
                .Select(d => Path.Combine(d, "mscordaccore.dll"))
                .FirstOrDefault(File.Exists);
        }
    }
}

