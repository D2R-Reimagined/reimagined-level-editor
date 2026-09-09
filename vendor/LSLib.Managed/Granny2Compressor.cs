using System.Runtime.InteropServices;

namespace LSLib.Native;

// The managed reader can decode uncompressed sections. Never silently load a native
// library from PATH, the game installation, or the preset's directory.
public static class Granny2Compressor
{
    private static nint library;
    private static readonly object Gate = new();
    public static bool IsConfigured { get { lock (Gate) return library != 0; } }
    public static void Configure(string absolutePath)
    {
        if (!Path.IsPathFullyQualified(absolutePath)) throw new ArgumentException("Use an absolute DLL path.");
        lock (Gate)
        {
            if (library != 0) throw new InvalidOperationException("Restart the editor to change the decoder.");
            var handle = NativeLibrary.Load(absolutePath);
            try
            {
                foreach (var symbol in new[] { "GrannyDecompressData", "GrannyBeginFileDecompression", "GrannyDecompressIncremental", "GrannyEndFileDecompression" })
                    NativeLibrary.GetExport(handle, symbol);
                library = handle;
            }
            catch { NativeLibrary.Free(handle); throw; }
        }
    }
    private static T Function<T>(string name) where T : Delegate
    {
        if (library == 0) throw new NotSupportedException("Compressed GR2: no Granny decoder is configured. Restore the bundled Native/granny2.dll or select a 64-bit decoder and reload.");
        return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate byte DecompressData(int format, byte reversed, int length, byte* input, int stop0, int stop1, int stop2, byte* output);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate nint Begin(int format, byte reversed, int size, byte* output, int workSize, byte* work);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate byte Increment(nint state, int length, byte* input);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate byte End(nint state);

    public static unsafe byte[] Decompress(int format, byte[] input, int size, int stop0, int stop1, int stop2)
    {
        lock (Gate)
        {
            var fn = Function<DecompressData>("GrannyDecompressData");
            var output = new byte[size];
            fixed (byte* src = input, dst = output)
                if (fn(format, 0, input.Length, src, stop0, stop1, stop2, dst) == 0)
                    throw new InvalidDataException("Granny decompression failed.");
            return output;
        }
    }

    public static unsafe byte[] Decompress4(byte[] input, int size)
    {
        lock (Gate)
        {
            var begin = Function<Begin>("GrannyBeginFileDecompression");
            var increment = Function<Increment>("GrannyDecompressIncremental");
            var end = Function<End>("GrannyEndFileDecompression");
            var output = new byte[size];
            var work = new byte[0x4000];
            fixed (byte* src = input, dst = output, scratch = work)
            {
                var state = begin(4, 0, size, dst, work.Length, scratch);
                if (state == 0) throw new InvalidDataException("Granny could not begin decompression.");
                bool ok = true;
                try
                {
                    for (var pos = 0; pos < input.Length; pos += 0x2000)
                        if (increment(state, Math.Min(0x2000, input.Length - pos), src + pos) == 0)
                        { ok = false; break; }
                }
                finally { ok &= end(state) != 0; }
                if (!ok) throw new InvalidDataException("Granny incremental decompression failed.");
            }
            return output;
        }
    }
}
