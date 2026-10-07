using System.Runtime.InteropServices;
namespace FrameCastStudio.Core.Encoding;

[ComImport, Guid("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecApi
{
    [PreserveSig] int IsSupported(ref Guid api);
    [PreserveSig] int IsModifiable(ref Guid api);
    [PreserveSig] int GetParameterRange(ref Guid api, out PropVariant min, out PropVariant max, out PropVariant step);
    [PreserveSig] int GetParameterValues(ref Guid api, out IntPtr values, out uint count);
    [PreserveSig] int GetDefaultValue(ref Guid api, out PropVariant value);
    [PreserveSig] int GetValue(ref Guid api, out PropVariant value);
    void SetValue(ref Guid api, ref PropVariant value);
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public ulong u64;
    public PropVariant(object v)
    {
        vt = 0; u64 = 0;
        switch (v)
        {
            case bool b: vt = 11; u64 = b ? 0xFFFFUL : 0; break;
            case uint u: vt = 19; u64 = u; break;
            case ulong l: vt = 21; u64 = l; break;
        }
    }
}

internal static class CodecApiGuids
{
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid MeanBitRate     = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid GopSize         = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid MaxBFrames      = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid LowLatencyMode  = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid DefaultQp       = new("2cb5696b-23fb-4ce1-a0f9-ef5b90fd55ca");
    public static readonly Guid BufferSize      = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    public static readonly Guid CommonQuality   = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
}
