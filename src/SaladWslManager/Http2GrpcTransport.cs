using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;

internal static partial class Program
{
    private static string CallGrpcHttp2UnaryEmpty(string host, int port, string path)
    {
        using (var client = new TcpClient())
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 15000;
            client.SendTimeout = 15000;
            ConnectTcp(client, host, port, TimeSpan.FromSeconds(5));

            using (var stream = client.GetStream())
            {
                var preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
                stream.Write(preface, 0, preface.Length);
                WriteHttp2Frame(stream, 0x4, 0x0, 0, new byte[0]);
                WaitForServerHttp2Settings(stream);

                var headers = BuildGrpcRequestHeaders(host + ":" + port.ToString(CultureInfo.InvariantCulture), path);
                WriteHttp2Frame(stream, 0x1, 0x4, 1, headers);

                var grpcEmptyBody = new byte[] { 0, 0, 0, 0, 0 };
                WriteHttp2Frame(stream, 0x0, 0x1, 1, grpcEmptyBody);
                stream.Flush();

                string httpStatus = null;
                string grpcStatus = null;
                string grpcMessage = null;
                var sawEndStream = false;

                while (!sawEndStream)
                {
                    var frame = ReadHttp2Frame(stream);
                    if (frame.Type == 0x4 && frame.StreamId == 0 && (frame.Flags & 0x1) == 0)
                    {
                        WriteHttp2Frame(stream, 0x4, 0x1, 0, new byte[0]);
                        continue;
                    }

                    if (frame.Type == 0x3 && frame.StreamId == 1)
                    {
                        return "h2 reset";
                    }

                    if (frame.Type == 0x7)
                    {
                        return "h2 goaway " + DecodeHttp2GoAway(frame.Payload);
                    }

                    if (frame.StreamId != 1)
                    {
                        continue;
                    }

                    if (frame.Type == 0x1)
                    {
                        var decoded = DecodeKnownHpackHeaders(frame.Payload);
                        if (decoded.HttpStatus != null)
                        {
                            httpStatus = decoded.HttpStatus;
                        }

                        if (decoded.GrpcStatus != null)
                        {
                            grpcStatus = decoded.GrpcStatus;
                        }

                        if (decoded.GrpcMessage != null)
                        {
                            grpcMessage = decoded.GrpcMessage;
                        }
                    }

                    if ((frame.Flags & 0x1) != 0)
                    {
                        sawEndStream = true;
                    }
                }

                if (grpcStatus == null)
                {
                    grpcStatus = "unknown";
                }

                if (grpcStatus != "0" && !string.IsNullOrEmpty(grpcMessage))
                {
                    return "h2 http=" + (httpStatus ?? "unknown") + " grpc=" + grpcStatus + " message=" + OneLine(grpcMessage);
                }

                return "h2 http=" + (httpStatus ?? "unknown") + " grpc=" + grpcStatus;
            }
        }
    }

    private static void ConnectTcp(TcpClient client, string host, int port, TimeSpan timeout)
    {
        var asyncResult = client.BeginConnect(host, port, null, null);
        if (!asyncResult.AsyncWaitHandle.WaitOne(timeout))
        {
            try
            {
                client.Close();
            }
            catch
            {
            }

            throw new IOException("tcp connect timeout " + host + ":" + port.ToString(CultureInfo.InvariantCulture));
        }

        client.EndConnect(asyncResult);
    }

    private static void WaitForServerHttp2Settings(Stream stream)
    {
        while (true)
        {
            var frame = ReadHttp2Frame(stream);
            if (frame.Type == 0x4 && frame.StreamId == 0)
            {
                if ((frame.Flags & 0x1) == 0)
                {
                    WriteHttp2Frame(stream, 0x4, 0x1, 0, new byte[0]);
                }

                return;
            }

            if (frame.Type == 0x7)
            {
                throw new IOException("h2 goaway before settings " + DecodeHttp2GoAway(frame.Payload));
            }
        }
    }

    private static byte[] BuildGrpcRequestHeaders(string authority, string path)
    {
        var buffer = new MemoryStream();
        WriteHpackIndexed(buffer, 3);
        WriteHpackIndexed(buffer, 6);
        WriteHpackLiteralIndexedName(buffer, 4, path);
        WriteHpackLiteralIndexedName(buffer, 1, authority);
        WriteHpackLiteralNewName(buffer, "content-type", "application/grpc+proto");
        WriteHpackLiteralNewName(buffer, "te", "trailers");
        WriteHpackLiteralNewName(buffer, "user-agent", "SaladWslManager");
        return buffer.ToArray();
    }

    private static void WriteHpackIndexed(Stream stream, int index)
    {
        WriteHpackInteger(stream, 0x80, 7, index);
    }

    private static void WriteHpackLiteralIndexedName(Stream stream, int nameIndex, string value)
    {
        WriteHpackInteger(stream, 0x00, 4, nameIndex);
        WriteHpackString(stream, value);
    }

    private static void WriteHpackLiteralNewName(Stream stream, string name, string value)
    {
        stream.WriteByte(0x00);
        WriteHpackString(stream, name);
        WriteHpackString(stream, value);
    }

    private static void WriteHpackString(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        WriteHpackInteger(stream, 0x00, 7, bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteHpackInteger(Stream stream, int mask, int prefixBits, int value)
    {
        var maxPrefix = (1 << prefixBits) - 1;
        if (value < maxPrefix)
        {
            stream.WriteByte((byte)(mask | value));
            return;
        }

        stream.WriteByte((byte)(mask | maxPrefix));
        value -= maxPrefix;
        while (value >= 128)
        {
            stream.WriteByte((byte)((value % 128) + 128));
            value /= 128;
        }

        stream.WriteByte((byte)value);
    }

    private static void WriteHttp2Frame(Stream stream, byte type, byte flags, int streamId, byte[] payload)
    {
        var length = payload == null ? 0 : payload.Length;
        var header = new byte[9];
        header[0] = (byte)((length >> 16) & 0xff);
        header[1] = (byte)((length >> 8) & 0xff);
        header[2] = (byte)(length & 0xff);
        header[3] = type;
        header[4] = flags;
        header[5] = (byte)((streamId >> 24) & 0x7f);
        header[6] = (byte)((streamId >> 16) & 0xff);
        header[7] = (byte)((streamId >> 8) & 0xff);
        header[8] = (byte)(streamId & 0xff);
        stream.Write(header, 0, header.Length);
        if (length > 0)
        {
            stream.Write(payload, 0, payload.Length);
        }
    }

    private static Http2Frame ReadHttp2Frame(Stream stream)
    {
        var header = ReadExact(stream, 9);
        var length = (header[0] << 16) | (header[1] << 8) | header[2];
        var streamId = ((header[5] & 0x7f) << 24) | (header[6] << 16) | (header[7] << 8) | header[8];
        var payload = length == 0 ? new byte[0] : ReadExact(stream, length);
        return new Http2Frame(header[3], header[4], streamId, payload);
    }

    private static string DecodeHttp2GoAway(byte[] payload)
    {
        if (payload == null || payload.Length < 8)
        {
            return "";
        }

        var lastStreamId = ((payload[0] & 0x7f) << 24) | (payload[1] << 16) | (payload[2] << 8) | payload[3];
        var errorCode = ((long)payload[4] << 24) | ((long)payload[5] << 16) | ((long)payload[6] << 8) | payload[7];
        var debug = payload.Length > 8 ? Encoding.ASCII.GetString(payload, 8, payload.Length - 8) : "";
        return string.Format(CultureInfo.InvariantCulture, "last={0} error={1} debug={2}", lastStreamId, errorCode, OneLine(debug));
    }

    private static byte[] ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = stream.Read(buffer, offset, length - offset);
            if (read <= 0)
            {
                throw new IOException("connection closed");
            }

            offset += read;
        }

        return buffer;
    }

    private static DecodedGrpcHeaders DecodeKnownHpackHeaders(byte[] payload)
    {
        var result = new DecodedGrpcHeaders();
        var index = 0;
        while (index < payload.Length)
        {
            var first = payload[index];
            if ((first & 0x80) != 0)
            {
                var tableIndex = ReadHpackInteger(payload, ref index, 7);
                ApplyStaticHeader(result, tableIndex, null);
                continue;
            }

            if ((first & 0x40) != 0)
            {
                ReadLiteralHeader(payload, ref index, 6, result);
                continue;
            }

            if ((first & 0x20) != 0)
            {
                index++;
                continue;
            }

            ReadLiteralHeader(payload, ref index, 4, result);
        }

        return result;
    }

    private static void ReadLiteralHeader(byte[] payload, ref int index, int namePrefixBits, DecodedGrpcHeaders result)
    {
        var nameIndex = ReadHpackInteger(payload, ref index, namePrefixBits);
        string name;
        if (nameIndex == 0)
        {
            name = ReadHpackString(payload, ref index);
        }
        else
        {
            name = GetStaticHeaderName(nameIndex);
        }

        var value = ReadHpackString(payload, ref index);
        ApplyHeader(result, name, value);
    }

    private static int ReadHpackInteger(byte[] payload, ref int index, int prefixBits)
    {
        var first = payload[index++];
        var maxPrefix = (1 << prefixBits) - 1;
        var value = first & maxPrefix;
        if (value < maxPrefix)
        {
            return value;
        }

        var shift = 0;
        byte b;
        do
        {
            b = payload[index++];
            value += (b & 0x7f) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0 && index < payload.Length);

        return value;
    }

    private static string ReadHpackString(byte[] payload, ref int index)
    {
        var huffman = (payload[index] & 0x80) != 0;
        var length = ReadHpackInteger(payload, ref index, 7);
        if (index + length > payload.Length)
        {
            index = payload.Length;
            return "";
        }

        var bytes = new byte[length];
        Buffer.BlockCopy(payload, index, bytes, 0, length);
        index += length;
        if (huffman)
        {
            return "";
        }

        return Encoding.ASCII.GetString(bytes);
    }

    private static void ApplyStaticHeader(DecodedGrpcHeaders result, int tableIndex, string valueOverride)
    {
        if (tableIndex == 8)
        {
            result.HttpStatus = valueOverride ?? "200";
        }
        else if (tableIndex == 9)
        {
            result.HttpStatus = valueOverride ?? "204";
        }
        else if (tableIndex == 10)
        {
            result.HttpStatus = valueOverride ?? "206";
        }
        else if (tableIndex == 11)
        {
            result.HttpStatus = valueOverride ?? "304";
        }
        else if (tableIndex == 12)
        {
            result.HttpStatus = valueOverride ?? "400";
        }
        else if (tableIndex == 13)
        {
            result.HttpStatus = valueOverride ?? "404";
        }
        else if (tableIndex == 14)
        {
            result.HttpStatus = valueOverride ?? "500";
        }
    }

    private static string GetStaticHeaderName(int tableIndex)
    {
        if (tableIndex == 8 || tableIndex == 9 || tableIndex == 10 ||
            tableIndex == 11 || tableIndex == 12 || tableIndex == 13 || tableIndex == 14)
        {
            return ":status";
        }

        if (tableIndex == 31)
        {
            return "content-type";
        }

        return "";
    }

    private static void ApplyHeader(DecodedGrpcHeaders result, string name, string value)
    {
        if (string.Equals(name, ":status", StringComparison.OrdinalIgnoreCase))
        {
            result.HttpStatus = value;
        }
        else if (string.Equals(name, "grpc-status", StringComparison.OrdinalIgnoreCase))
        {
            result.GrpcStatus = value;
        }
        else if (string.Equals(name, "grpc-message", StringComparison.OrdinalIgnoreCase))
        {
            result.GrpcMessage = Uri.UnescapeDataString(value ?? "");
        }
    }

    private struct Http2Frame
    {
        public readonly byte Type;
        public readonly byte Flags;
        public readonly int StreamId;
        public readonly byte[] Payload;

        public Http2Frame(byte type, byte flags, int streamId, byte[] payload)
        {
            Type = type;
            Flags = flags;
            StreamId = streamId;
            Payload = payload;
        }
    }

    private sealed class DecodedGrpcHeaders
    {
        public string HttpStatus;
        public string GrpcStatus;
        public string GrpcMessage;
    }
}
