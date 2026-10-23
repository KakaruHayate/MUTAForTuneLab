using System.Runtime.InteropServices;
using System.Text;

namespace MutaForTuneLab.Native;

/// <summary>
/// MUTA 的 .chr / .inf 里的名字是 GBK（代码页 936）。.NET 的 GBK 解码需要
/// System.Text.Encoding.CodePages 包，为省一个随包依赖，直接用 Windows 的
/// MultiByteToWideChar。本插件本来就只发 win-x64。
/// </summary>
internal static class Gbk
{
    const int CP_GBK = 936;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern int MultiByteToWideChar(
        int codePage, uint flags, byte[] multiByteStr, int cbMultiByte,
        [Out] char[] wideCharStr, int cchWideChar);

    /// <summary>把 GBK 字节解成字符串；失败时回退 UTF-8，再失败返回 null。</summary>
    public static string? TryDecode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return null;
        var buffer = bytes.ToArray();
        try
        {
            int needed = MultiByteToWideChar(CP_GBK, 0, buffer, buffer.Length, null!, 0);
            if (needed > 0)
            {
                var chars = new char[needed];
                int written = MultiByteToWideChar(CP_GBK, 0, buffer, buffer.Length, chars, needed);
                if (written > 0)
                    return new string(chars, 0, written).Trim();
            }
        }
        catch (DllNotFoundException)
        {
            // 非 Windows：退到 UTF-8。
        }
        catch (EntryPointNotFoundException)
        {
        }

        try
        {
            return Encoding.UTF8.GetString(buffer).Trim();
        }
        catch
        {
            return null;
        }
    }
}
