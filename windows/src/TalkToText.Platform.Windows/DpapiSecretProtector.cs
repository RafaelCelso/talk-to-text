using System.ComponentModel;
using System.Runtime.InteropServices;
using TalkToText.Abstractions;

namespace TalkToText.Platform.Windows;

/// <summary>DPAPI, scoped to the current user: the API keys of the cloud AI, encrypted at rest.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <inheritdoc />
    public byte[] Protect(byte[] data) => Run(data, protect: true);

    /// <inheritdoc />
    public byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[] Run(byte[] data, bool protect)
    {
        ArgumentNullException.ThrowIfNull(data);
        var input = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        var output = default(DataBlob);
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var ok = protect
                ? CryptProtectData(ref input, "Talk-to-Text", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
}
