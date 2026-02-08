namespace Gridder.Helpers;

public static class BigEndianHelper
{
    public static byte[] GetBytesBigEndian(float value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return bytes;
    }

    public static byte[] GetBytesBigEndian(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return bytes;
    }

    public static float ReadFloatBigEndian(ReadOnlySpan<byte> data)
    {
        Span<byte> tmp = stackalloc byte[4];
        data[..4].CopyTo(tmp);
        if (BitConverter.IsLittleEndian) tmp.Reverse();
        return BitConverter.ToSingle(tmp);
    }

    public static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data)
    {
        Span<byte> tmp = stackalloc byte[4];
        data[..4].CopyTo(tmp);
        if (BitConverter.IsLittleEndian) tmp.Reverse();
        return BitConverter.ToUInt32(tmp);
    }
}
