using System.Reflection;
using System.Runtime.InteropServices;

namespace FanControl.MsiEc;

/// <summary>
/// P/Invoke bindings for PawnIOLib (https://pawnio.eu), the user-mode API of the
/// signed, HVCI-compatible PawnIO kernel driver.
/// </summary>
internal static class PawnIoNative
{
    private const string LibName = "PawnIOLib";

    static PawnIoNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(PawnIoNative).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != LibName)
            return IntPtr.Zero;

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll"),
            "PawnIOLib.dll",
        ];

        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    [DllImport(LibName, ExactSpelling = true)]
    internal static extern int pawnio_version(out uint version);

    [DllImport(LibName, ExactSpelling = true)]
    internal static extern int pawnio_open(out IntPtr handle);

    [DllImport(LibName, ExactSpelling = true)]
    internal static extern int pawnio_load(IntPtr handle, byte[] blob, nuint size);

    [DllImport(LibName, ExactSpelling = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    internal static extern int pawnio_execute(
        IntPtr handle,
        string name,
        ulong[] inBuf,
        nuint inSize,
        ulong[] outBuf,
        nuint outSize,
        out nuint returnSize);

    [DllImport(LibName, ExactSpelling = true)]
    internal static extern int pawnio_close(IntPtr handle);
}
