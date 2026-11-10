using System.Runtime.InteropServices;
using System.Text;

namespace Habilis.GitHub;

/// <summary>Where the sign-in token is kept between runs.</summary>
internal interface ITokenStore
{
    string? Read();

    void Write(string login, string token);

    void Delete();
}

/// <summary>
/// The token in Windows Credential Manager, as a generic credential "HabilisTerminal:github" with the GitHub login as
/// its user name. It is visible and removable in Control Panel > Credential Manager, and never written anywhere else.
/// </summary>
internal sealed partial class CredentialManagerStore : ITokenStore
{
    public const string Target = "HabilisTerminal:github";

    private const uint GenericType = 1;
    private const uint PersistLocalMachine = 2;

    public string? Read()
    {
        if (!CredRead(Target, GenericType, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.BlobSize == 0 || credential.Blob == IntPtr.Zero)
            {
                return null;
            }

            var bytes = new byte[credential.BlobSize];
            Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Write(string login, string token)
    {
        var bytes = Encoding.Unicode.GetBytes(token);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        var target = Marshal.StringToHGlobalUni(Target);
        var user = Marshal.StringToHGlobalUni(login);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = GenericType,
                TargetName = target,
                UserName = user,
                Blob = blob,
                BlobSize = (uint)bytes.Length,
                Persist = PersistLocalMachine,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new HabilisTerminal.Plugins.PluginException($"Windows did not keep the GitHub sign-in (error {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            // The token's bytes do not stay in unmanaged memory.
            Marshal.Copy(new byte[bytes.Length], 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(user);
        }
    }

    public void Delete() => CredDelete(Target, GenericType, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref Credential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(IntPtr buffer);
}
