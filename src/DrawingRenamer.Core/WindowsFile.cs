using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DrawingRenamer;

internal static class WindowsFile
{
    // DELETE access lets the same handle verify and then rename or remove the file.
    internal static FileStream OpenForMove(string path)
    {
        path = Path.GetFullPath(path);
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        const uint readAndDelete = 0x80000000 | 0x10000;
        var handle = CreateFileW(path, readAndDelete, FileShare.Read, 0, FileMode.Open, 0, 0);
        if (handle.IsInvalid) throw LastFileError();
        return new FileStream(handle, FileAccess.Read);
    }

    internal static void Delete(FileStream file)
    {
        const int fileDispositionInfo = 4;
        if (!SetFileInformationByHandle(file.SafeFileHandle, fileDispositionInfo, [1], 1))
            throw LastFileError();
    }

    internal static bool TryRenameOnSameVolume(FileStream file, string target)
    {
        const int fileRenameInfo = 3;
        const int notSameDevice = 17;
        // FILE_RENAME_INFO has a flags word, an aligned HANDLE, a byte count and the UTF-16 name.
        var lengthOffset = 2 * IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(int);
        var length = Encoding.Unicode.GetByteCount(target);
        var info = new byte[nameOffset + length + sizeof(char)];
        BitConverter.TryWriteBytes(info.AsSpan(lengthOffset), length);
        Encoding.Unicode.GetBytes(target, info.AsSpan(nameOffset));
        if (SetFileInformationByHandle(file.SafeFileHandle, fileRenameInfo, info, info.Length)) return true;
        if (Marshal.GetLastPInvokeError() == notSameDevice) return false;
        throw LastFileError();
    }

    private static Exception LastFileError()
    {
        var error = Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error())!;
        return error is IOException or UnauthorizedAccessException ? error : new IOException(error.Message, error);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share,
        nint security, FileMode disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, byte[] data, int size);
}
