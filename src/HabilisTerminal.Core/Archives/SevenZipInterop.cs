using System.Runtime.InteropServices;

namespace HabilisTerminal.Core.Archives;

// The 7z.dll interfaces used for reading archives (7-Zip's IArchive.h, IStream.h, IPassword.h). All are IUnknown
// based, return HRESULTs and take raw pointers where 7-Zip passes optional values.

[ComImport, Guid("23170F69-40C1-278A-0000-000300030000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInStream
{
    [PreserveSig]
    int Read(IntPtr data, uint size, IntPtr processedSize);

    [PreserveSig]
    int Seek(long offset, uint seekOrigin, IntPtr newPosition);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000300020000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISequentialOutStream
{
    [PreserveSig]
    int Write(IntPtr data, uint size, IntPtr processedSize);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600100000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveOpenCallback
{
    [PreserveSig]
    int SetTotal(IntPtr files, IntPtr bytes);

    [PreserveSig]
    int SetCompleted(IntPtr files, IntPtr bytes);
}

/// <summary>Extends IProgress (SetTotal, SetCompleted), declared here in vtable order.</summary>
[ComImport, Guid("23170F69-40C1-278A-0000-000600200000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IArchiveExtractCallback
{
    [PreserveSig]
    int SetTotal(ulong total);

    [PreserveSig]
    int SetCompleted(IntPtr completeValue);

    [PreserveSig]
    int GetStream(uint index, out ISequentialOutStream? outStream, int askExtractMode);

    [PreserveSig]
    int PrepareOperation(int askExtractMode);

    [PreserveSig]
    int SetOperationResult(int operationResult);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000500100000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICryptoGetTextPassword
{
    [PreserveSig]
    int CryptoGetTextPassword([MarshalAs(UnmanagedType.BStr)] out string? password);
}

[ComImport, Guid("23170F69-40C1-278A-0000-000600600000"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInArchive
{
    [PreserveSig]
    int Open(IInStream stream, IntPtr maxCheckStartPosition, IArchiveOpenCallback? callback);

    [PreserveSig]
    int Close();

    [PreserveSig]
    int GetNumberOfItems(out uint count);

    [PreserveSig]
    int GetProperty(uint index, uint propId, IntPtr value);

    [PreserveSig]
    int Extract([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] uint[]? indices, uint count, int testMode, IArchiveExtractCallback callback);

    [PreserveSig]
    int GetArchiveProperty(uint propId, IntPtr value);
}

/// <summary>Property ids (kpid*) and handler property ids from 7-Zip's PropID.h and IArchive.h.</summary>
internal static class SevenZipIds
{
    public const uint Path = 3;
    public const uint IsDir = 6;
    public const uint Size = 7;
    public const uint PackSize = 8;
    public const uint Attrib = 9;
    public const uint MTime = 12;
    public const uint Encrypted = 15;

    public const uint HandlerName = 0;
    public const uint HandlerClassId = 1;
    public const uint HandlerExtension = 2;

    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_ABORT = unchecked((int)0x80004004);
    public const int E_FAIL = unchecked((int)0x80004005);

    /// <summary>NArchive::NExtract::NAskMode::kExtract.</summary>
    public const int AskExtract = 0;

    /// <summary>NArchive::NExtract::NOperationResult values.</summary>
    public const int ResultOk = 0;
    public const int ResultUnsupportedMethod = 1;
    public const int ResultDataError = 2;
    public const int ResultCrcError = 3;
    public const int ResultWrongPassword = 9;

    public static readonly Guid InArchive = typeof(IInArchive).GUID;
}

/// <summary>A PROPVARIANT in unmanaged memory: read once, then cleared.</summary>
internal sealed class PropVariant : IDisposable
{
    // 24 bytes on 64-bit Windows; 32 leaves room.
    private const int SizeInBytes = 32;

    private const ushort VtEmpty = 0;
    private const ushort VtI4 = 3;
    private const ushort VtBstr = 8;
    private const ushort VtBool = 11;
    private const ushort VtUi2 = 18;
    private const ushort VtUi4 = 19;
    private const ushort VtI8 = 20;
    private const ushort VtUi8 = 21;
    private const ushort VtFileTime = 64;

    public PropVariant()
    {
        Pointer = Marshal.AllocCoTaskMem(SizeInBytes);
        for (var i = 0; i < SizeInBytes; i += 8)
        {
            Marshal.WriteInt64(Pointer, i, 0);
        }
    }

    public IntPtr Pointer { get; }

    private ushort Type => (ushort)Marshal.ReadInt16(Pointer);

    public bool IsEmpty => Type == VtEmpty;

    public string? AsString() => Type == VtBstr ? Marshal.PtrToStringBSTR(Marshal.ReadIntPtr(Pointer, 8)) : null;

    /// <summary>A BSTR holding raw bytes (handler class ids and signatures).</summary>
    public byte[]? AsBytes()
    {
        if (Type != VtBstr)
        {
            return null;
        }

        var bstr = Marshal.ReadIntPtr(Pointer, 8);
        if (bstr == IntPtr.Zero)
        {
            return null;
        }

        var length = Marshal.ReadInt32(bstr, -4);
        var bytes = new byte[length];
        Marshal.Copy(bstr, bytes, 0, length);
        return bytes;
    }

    public bool AsBool() => Type == VtBool && Marshal.ReadInt16(Pointer, 8) != 0;

    public ulong? AsUInt64() => Type switch
    {
        VtUi2 => (ushort)Marshal.ReadInt16(Pointer, 8),
        VtUi4 => (uint)Marshal.ReadInt32(Pointer, 8),
        VtI4 => (ulong)Math.Max(0, Marshal.ReadInt32(Pointer, 8)),
        VtUi8 or VtI8 => (ulong)Marshal.ReadInt64(Pointer, 8),
        _ => null,
    };

    public DateTime? AsFileTimeUtc()
    {
        if (Type != VtFileTime)
        {
            return null;
        }

        var value = Marshal.ReadInt64(Pointer, 8);
        return value > 0 && value < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(value) : null;
    }

    public void Clear() => _ = PropVariantClear(Pointer);

    public void Dispose()
    {
        Clear();
        Marshal.FreeCoTaskMem(Pointer);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(IntPtr value);
}

/// <summary>A file stream as 7-Zip's IInStream.</summary>
[ComVisible(true)]
internal sealed class InStream(Stream stream) : IInStream, IDisposable
{
    private byte[] _buffer = new byte[64 * 1024];

    public int Read(IntPtr data, uint size, IntPtr processedSize)
    {
        try
        {
            if (_buffer.Length < size)
            {
                _buffer = new byte[size];
            }

            var total = 0;
            while (total < size)
            {
                var read = stream.Read(_buffer, total, (int)size - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            Marshal.Copy(_buffer, 0, data, total);
            if (processedSize != IntPtr.Zero)
            {
                Marshal.WriteInt32(processedSize, total);
            }

            return SevenZipIds.S_OK;
        }
        catch (IOException)
        {
            return SevenZipIds.E_FAIL;
        }
    }

    public int Seek(long offset, uint seekOrigin, IntPtr newPosition)
    {
        try
        {
            var position = stream.Seek(offset, (SeekOrigin)seekOrigin);
            if (newPosition != IntPtr.Zero)
            {
                Marshal.WriteInt64(newPosition, position);
            }

            return SevenZipIds.S_OK;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return SevenZipIds.E_FAIL;
        }
    }

    public void Dispose() => stream.Dispose();
}

/// <summary>Writes extracted bytes to a file, counting them for the progress.</summary>
[ComVisible(true)]
internal sealed class OutStream(Stream stream, Action<long> written) : ISequentialOutStream, IDisposable
{
    private byte[] _buffer = new byte[64 * 1024];

    public int Write(IntPtr data, uint size, IntPtr processedSize)
    {
        try
        {
            if (_buffer.Length < size)
            {
                _buffer = new byte[size];
            }

            Marshal.Copy(data, _buffer, 0, (int)size);
            stream.Write(_buffer, 0, (int)size);
            if (processedSize != IntPtr.Zero)
            {
                Marshal.WriteInt32(processedSize, (int)size);
            }

            written(size);
            return SevenZipIds.S_OK;
        }
        catch (IOException)
        {
            return SevenZipIds.E_FAIL;
        }
    }

    public void Dispose() => stream.Dispose();
}

/// <summary>Opening callback: answers password requests for archives with encrypted headers.</summary>
[ComVisible(true)]
internal sealed class OpenCallback(Func<string?> password) : IArchiveOpenCallback, ICryptoGetTextPassword
{
    public bool PasswordAsked { get; private set; }

    public int SetTotal(IntPtr files, IntPtr bytes) => SevenZipIds.S_OK;

    public int SetCompleted(IntPtr files, IntPtr bytes) => SevenZipIds.S_OK;

    public int CryptoGetTextPassword(out string? result)
    {
        PasswordAsked = true;
        result = password();
        return result is null ? SevenZipIds.E_ABORT : SevenZipIds.S_OK;
    }
}
