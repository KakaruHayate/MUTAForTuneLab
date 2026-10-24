using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MutaForTuneLab.Native;

/// <summary>
/// Finds the native libraries in the package folder.
///
/// The host runs from its own directory and our package folder is not on the process
/// search path, so every import is resolved by hand. A .NET assembly accepts exactly
/// one resolver, which is why all libraries share this one instead of each setting its
/// own.
/// </summary>
public static class NativeLibraries
{
    /// <summary>Import name → file name in the package folder.</summary>
    static readonly Dictionary<string, string> Libraries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["muta_hts"] = "muta_hts.dll",
        ["worldline"] = "worldline.dll",
        ["muta_sinsy"] = "muta_sinsy.dll",
    };

    static readonly object Gate = new();
    static bool mInstalled;

    /// <summary>Idempotent: safe to call from every type that imports a native symbol.</summary>
    public static void EnsureResolver()
    {
        if (mInstalled)
            return;
        lock (Gate)
        {
            if (mInstalled)
                return;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
                // Another type in this assembly already installed one; the resolver is
                // the same function, so nothing to do.
            }
            mInstalled = true;
        }
    }

    static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!Libraries.TryGetValue(name, out var file))
            return IntPtr.Zero;

        string directory = Path.GetDirectoryName(assembly.Location) ?? ".";
        string candidate = Path.Combine(directory, file);
        return File.Exists(candidate) ? NativeLibrary.Load(candidate) : IntPtr.Zero;
    }

    /// <summary>Where the package lives — useful for data files that ship beside the code.</summary>
    public static string PackageDirectory
        => Path.GetDirectoryName(typeof(NativeLibraries).Assembly.Location) ?? ".";
}
