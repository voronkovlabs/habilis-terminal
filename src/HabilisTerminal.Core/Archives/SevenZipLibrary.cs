using System.Runtime.InteropServices;

namespace HabilisTerminal.Core.Archives;

/// <summary>One archive format 7z.dll can read: its name, handler class id and usual extensions.</summary>
public sealed record ArchiveFormat(string Name, Guid ClassId, IReadOnlyList<string> Extensions);

/// <summary>
/// 7z.dll (7-Zip, LGPL), loaded once from the application folder (issue #51). Lists the formats it reads and opens
/// archives with them. Not available when the DLL is missing or does not match the process architecture.
/// </summary>
public sealed class SevenZipLibrary
{
    public const string FileName = "7z.dll";

    private static readonly Lazy<SevenZipLibrary?> Shared = new(() => TryLoad(Path.Combine(AppContext.BaseDirectory, FileName)));

    private readonly CreateObjectFunction _createObject;

    private SevenZipLibrary(IntPtr handle)
    {
        _createObject = Marshal.GetDelegateForFunctionPointer<CreateObjectFunction>(NativeLibrary.GetExport(handle, "CreateObject"));
        var getCount = Marshal.GetDelegateForFunctionPointer<GetNumberOfFormatsFunction>(NativeLibrary.GetExport(handle, "GetNumberOfFormats"));
        var getProperty = Marshal.GetDelegateForFunctionPointer<GetHandlerPropertyFunction>(NativeLibrary.GetExport(handle, "GetHandlerProperty2"));
        Formats = ReadFormats(getCount, getProperty);
        Extensions = new HashSet<string>(Formats.SelectMany(f => f.Extensions), StringComparer.OrdinalIgnoreCase);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateObjectFunction(ref Guid classId, ref Guid interfaceId, out IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetNumberOfFormatsFunction(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetHandlerPropertyFunction(uint formatIndex, uint propId, IntPtr value);

    /// <summary>The library next to the application, or null when it cannot be loaded.</summary>
    public static SevenZipLibrary? Default => Shared.Value;

    public IReadOnlyList<ArchiveFormat> Formats { get; }

    /// <summary>Every extension a format claims (zip, 7z, rar, tar, gz, iso...), without the dot.</summary>
    public IReadOnlySet<string> Extensions { get; }

    public static SevenZipLibrary? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) && NativeLibrary.TryLoad(path, out var handle) ? new SevenZipLibrary(handle) : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> with the formats its extension names; with <paramref name="anyFormat"/> every
    /// other format is tried after them (Ctrl+PgDn: by signature). Null when no format accepts the file.
    /// </summary>
    /// <param name="password">Asked when the archive's headers are encrypted; null cancels.</param>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public SevenZipArchive? Open(string path, bool anyFormat, Func<string?>? password = null)
    {
        var extension = Path.GetExtension(path).TrimStart('.');
        var byExtension = Formats.Where(f => f.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)).ToList();
        var candidates = anyFormat ? byExtension.Concat(Formats.Except(byExtension)) : byExtension;
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
        var input = new InStream(stream);
        string? remembered = null;
        var callback = new OpenCallback(() => remembered ??= password?.Invoke());
        foreach (var format in candidates)
        {
            var archive = Create(format.ClassId);
            if (archive is null)
            {
                continue;
            }

            stream.Position = 0;
            var maxCheck = Marshal.AllocCoTaskMem(sizeof(ulong));
            try
            {
                Marshal.WriteInt64(maxCheck, 1 << 20);
                if (archive.Open(input, maxCheck, callback) == SevenZipIds.S_OK)
                {
                    return new SevenZipArchive(path, format, archive, input, remembered);
                }
            }
            catch (COMException)
            {
                // A format that fails on this file: try the next one.
            }
            finally
            {
                Marshal.FreeCoTaskMem(maxCheck);
            }

            Release(archive);
            if (callback.PasswordAsked && remembered is null)
            {
                // The user cancelled the password: stop trying other formats.
                break;
            }
        }

        input.Dispose();
        return null;
    }

    internal static void Release(IInArchive archive)
    {
        try
        {
            archive.Close();
        }
        finally
        {
            Marshal.FinalReleaseComObject(archive);
        }
    }

    private IInArchive? Create(Guid classId)
    {
        var interfaceId = SevenZipIds.InArchive;
        if (_createObject(ref classId, ref interfaceId, out var pointer) != SevenZipIds.S_OK || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return (IInArchive)Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static List<ArchiveFormat> ReadFormats(GetNumberOfFormatsFunction getCount, GetHandlerPropertyFunction getProperty)
    {
        var formats = new List<ArchiveFormat>();
        if (getCount(out var count) != SevenZipIds.S_OK)
        {
            return formats;
        }

        using var value = new PropVariant();
        for (uint i = 0; i < count; i++)
        {
            string? name = null;
            byte[]? classId = null;
            string? extensions = null;
            if (getProperty(i, SevenZipIds.HandlerName, value.Pointer) == SevenZipIds.S_OK)
            {
                name = value.AsString();
            }

            value.Clear();
            if (getProperty(i, SevenZipIds.HandlerClassId, value.Pointer) == SevenZipIds.S_OK)
            {
                classId = value.AsBytes();
            }

            value.Clear();
            if (getProperty(i, SevenZipIds.HandlerExtension, value.Pointer) == SevenZipIds.S_OK)
            {
                extensions = value.AsString();
            }

            value.Clear();
            if (name is not null && classId is { Length: 16 })
            {
                formats.Add(new ArchiveFormat(name, new Guid(classId), (extensions ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)));
            }
        }

        return formats;
    }
}
