using System;

internal static partial class Program
{
    private static bool TryReadProtoLengthDelimited(byte[] data, ref int index, int end, out byte[] value)
    {
        value = new byte[0];
        ulong length;
        if (!TryReadProtoVarint(data, ref index, end, out length) || length > int.MaxValue)
        {
            return false;
        }

        var count = (int)length;
        if (count < 0 || index + count > end)
        {
            return false;
        }

        value = new byte[count];
        Buffer.BlockCopy(data, index, value, 0, count);
        index += count;
        return true;
    }

    private static bool TryReadProtoDouble(byte[] data, ref int index, int end, out double value)
    {
        value = 0;
        if (index + 8 > end)
        {
            return false;
        }

        value = BitConverter.ToDouble(data, index);
        index += 8;
        return true;
    }

    private static bool TryReadProtoVarint(byte[] data, ref int index, int end, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (index < end && shift <= 63)
        {
            var b = data[index++];
            value |= ((ulong)(b & 0x7f)) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }

    private static bool SkipProtoField(byte[] data, ref int index, int end, int wireType)
    {
        ulong ignored;
        switch (wireType)
        {
            case 0:
                return TryReadProtoVarint(data, ref index, end, out ignored);
            case 1:
                index += 8;
                return index <= end;
            case 2:
                if (!TryReadProtoVarint(data, ref index, end, out ignored) || ignored > int.MaxValue)
                {
                    return false;
                }

                index += (int)ignored;
                return index <= end;
            case 5:
                index += 4;
                return index <= end;
            default:
                return false;
        }
    }
}
