using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using WorkToUnlock.Core.Rules;

namespace WorkToUnlock.Infrastructure.Windows;

/// <summary>
/// Reads the Authenticode signer of an executable with WinVerifyTrust. Results are cached per file version
/// (path + size + last write time). Revocation is not checked and no network retrieval is performed, so the
/// check is fast and works offline.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class AuthenticodeSignatureReader(ILogger<AuthenticodeSignatureReader> logger) : IExecutableSignatureReader
{
    private const int MaxCacheEntries = 2048;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ExecutableSignature? GetSignature(string executablePath)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(executablePath);
            if (!file.Exists)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        var stamp = (file.Length, file.LastWriteTimeUtc.Ticks);
        if (_cache.TryGetValue(file.FullName, out var cached) && cached.Stamp == stamp)
        {
            return cached.Signature;
        }

        ExecutableSignature signature;
        try
        {
            signature = Read(file.FullName);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Could not read the signature of {Path}", file.FullName);
            signature = new ExecutableSignature(null, false);
        }

        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }

        _cache[file.FullName] = new CacheEntry(stamp, signature);
        return signature;
    }

    private static ExecutableSignature Read(string path)
    {
        fixed (char* filePath = path)
        {
            var fileInfo = new NativeMethods.WINTRUST_FILE_INFO
            {
                cbStruct = (uint)sizeof(NativeMethods.WINTRUST_FILE_INFO),
                pcwszFilePath = filePath,
            };
            var data = new NativeMethods.WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(NativeMethods.WINTRUST_DATA),
                dwUIChoice = NativeMethods.WTD_UI_NONE,
                fdwRevocationChecks = NativeMethods.WTD_REVOKE_NONE,
                dwUnionChoice = NativeMethods.WTD_CHOICE_FILE,
                pFile = &fileInfo,
                dwStateAction = NativeMethods.WTD_STATEACTION_VERIFY,
                dwProvFlags = NativeMethods.WTD_REVOCATION_CHECK_NONE | NativeMethods.WTD_CACHE_ONLY_URL_RETRIEVAL,
            };
            var action = NativeMethods.WINTRUST_ACTION_GENERIC_VERIFY_V2;

            var result = NativeMethods.WinVerifyTrust(-1, &action, &data);
            try
            {
                return new ExecutableSignature(GetSignerName(data.hWVTStateData), result == 0);
            }
            finally
            {
                data.dwStateAction = NativeMethods.WTD_STATEACTION_CLOSE;
                NativeMethods.WinVerifyTrust(-1, &action, &data);
            }
        }
    }

    private static string? GetSignerName(nint stateData)
    {
        if (stateData == 0)
        {
            return null;
        }

        var providerData = NativeMethods.WTHelperProvDataFromStateData(stateData);
        if (providerData == 0)
        {
            return null;
        }

        var signer = NativeMethods.WTHelperGetProvSignerFromChain(providerData, 0, false, 0);
        if (signer == 0)
        {
            return null;
        }

        var head = Marshal.PtrToStructure<NativeMethods.CRYPT_PROVIDER_SGNR_HEAD>(signer);
        if (head.csCertChain == 0 || head.pasCertChain == 0)
        {
            return null;
        }

        var cert = Marshal.PtrToStructure<NativeMethods.CRYPT_PROVIDER_CERT_HEAD>(head.pasCertChain);
        if (cert.pCert == 0)
        {
            return null;
        }

        var length = NativeMethods.CertGetNameStringW(cert.pCert, NativeMethods.CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, 0, null, 0);
        if (length <= 1)
        {
            return null;
        }

        var buffer = new char[length];
        fixed (char* p = buffer)
        {
            NativeMethods.CertGetNameStringW(cert.pCert, NativeMethods.CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, 0, p, length);
        }

        return new string(buffer, 0, (int)length - 1);
    }

    private sealed record CacheEntry((long Length, long Ticks) Stamp, ExecutableSignature Signature);
}
