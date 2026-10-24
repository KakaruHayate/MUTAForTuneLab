using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MutaForTuneLab.Native;

/// <summary>
/// Managed face of <c>muta_hts.dll</c> — the HTS engine wrapped so that one instance
/// owns all of its state.
///
/// The native library ships next to this assembly (the package root), so a resolver
/// points the import at that folder rather than relying on the process search path:
/// the host runs from its own directory and the package folder is not on it.
/// </summary>
public sealed class MutaHts : IDisposable
{
    const string Library = "muta_hts";

    static MutaHts()
    {
        NativeLibrary.SetDllImportResolver(typeof(MutaHts).Assembly, Resolve);
    }

    static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!name.Equals(Library, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        string directory = Path.GetDirectoryName(assembly.Location) ?? ".";
        string candidate = Path.Combine(directory, "muta_hts.dll");
        return File.Exists(candidate) ? NativeLibrary.Load(candidate) : IntPtr.Zero;
    }

    IntPtr mHandle;

    MutaHts(IntPtr handle) => mHandle = handle;

    /// <summary>Loads an HTS voice. <paramref name="enginePath"/> must be a decoded file.</summary>
    public static MutaHts Load(string enginePath)
    {
        var handle = muta_hts_create(enginePath);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"cannot load HTS voice: {enginePath}");
        return new MutaHts(handle);
    }

    public void Dispose()
    {
        if (mHandle != IntPtr.Zero)
        {
            muta_hts_destroy(mHandle);
            mHandle = IntPtr.Zero;
        }
    }

    // ---- synthesis ----

    /// <summary>Runs the engine over a label sequence; returns the frame count.</summary>
    public int Synthesize(IReadOnlyList<string> labels)
    {
        if (labels.Count == 0)
            return 0;

        var pointers = new IntPtr[labels.Count];
        IntPtr array = Marshal.AllocHGlobal(IntPtr.Size * labels.Count);
        try
        {
            for (int i = 0; i < labels.Count; i++)
            {
                pointers[i] = Marshal.StringToCoTaskMemUTF8(labels[i]);
                Marshal.WriteIntPtr(array, i * IntPtr.Size, pointers[i]);
            }

            int frames = muta_hts_synthesize(mHandle, array, labels.Count);
            if (frames < 0)
                throw new InvalidOperationException("HTS synthesis failed");
            return frames;
        }
        finally
        {
            foreach (var pointer in pointers)
                if (pointer != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(pointer);
            Marshal.FreeHGlobal(array);
        }
    }

    /// <summary>
    /// Copies one stream's generated parameters into <paramref name="destination"/> as
    /// [frame][vector] rows. The buffer must hold frames × <see cref="VectorLength"/>.
    /// </summary>
    public void GetStream(int stream, double[] destination)
    {
        int expected = TotalFrame * VectorLength(stream);
        if (destination.Length < expected)
            throw new ArgumentException($"buffer holds {destination.Length}, need {expected}");
        int written = muta_hts_get_stream(mHandle, stream, destination);
        if (written != expected)
            throw new InvalidOperationException($"stream {stream}: wrote {written}, expected {expected}");
    }

    public int[] StateDurations()
    {
        int states = TotalState;
        var result = new int[states];
        for (int i = 0; i < states; i++)
            result[i] = muta_hts_state_duration(mHandle, i);
        return result;
    }

    // ---- knobs ----

    public double Speed
    {
        set => muta_hts_set_speed(mHandle, value);
    }

    public void SetGvWeight(int stream, double weight) => muta_hts_set_gv_weight(mHandle, stream, weight);

    public double GetGvWeight(int stream) => muta_hts_get_gv_weight(mHandle, stream);

    public void SetMsdThreshold(int stream, double value) => muta_hts_set_msd_threshold(mHandle, stream, value);

    public bool PhonemeAlignment
    {
        set => muta_hts_set_phoneme_alignment(mHandle, value ? 1 : 0);
    }

    /// <summary>1 = voiced, 0 = unvoiced, one entry per note. Drives the per-note MSD rule.</summary>
    public void SetNoteFlags(ReadOnlySpan<int> flags, int statesPerNote)
    {
        var copy = flags.ToArray();
        if (muta_hts_set_note_flags(mHandle, copy, copy.Length, statesPerNote) < 0)
            throw new ArgumentException("note flags rejected");
    }

    // ---- queries ----

    public int SampleRate => muta_hts_sampling_frequency(mHandle);
    public int FramePeriod => muta_hts_frame_period(mHandle);
    public int States => muta_hts_num_states(mHandle);
    public int Streams => muta_hts_num_streams(mHandle);
    public int TotalFrame => muta_hts_total_frame(mHandle);
    public int TotalState => muta_hts_total_state(mHandle);
    public int VectorLength(int stream) => muta_hts_vector_length(mHandle, stream);

    // ---- imports ----

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr muta_hts_create([MarshalAs(UnmanagedType.LPUTF8Str)] string voicePath);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void muta_hts_destroy(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int muta_hts_synthesize(IntPtr handle, IntPtr labels, int count);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int muta_hts_get_stream(IntPtr handle, int stream, double[] output);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int muta_hts_state_duration(IntPtr handle, int state);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int muta_hts_total_frame(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int muta_hts_total_state(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int muta_hts_num_states(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int muta_hts_num_streams(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int muta_hts_vector_length(IntPtr handle, int stream);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int muta_hts_sampling_frequency(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int muta_hts_frame_period(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void muta_hts_set_speed(IntPtr handle, double speed);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void muta_hts_set_gv_weight(IntPtr handle, int stream, double weight);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern double muta_hts_get_gv_weight(IntPtr handle, int stream);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void muta_hts_set_msd_threshold(IntPtr handle, int stream, double value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern void muta_hts_set_phoneme_alignment(IntPtr handle, int enabled);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    static extern int muta_hts_set_note_flags(IntPtr handle, int[] flags, int count, int statesPerNote);
}
