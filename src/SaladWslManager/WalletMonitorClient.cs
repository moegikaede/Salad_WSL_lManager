using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

internal static partial class Program
{
    private static void QueueWalletBalanceRefreshIfNeeded(bool saladRunning)
    {
        if (!saladRunning)
        {
            // Balance and Last24h are read-only snapshots; keep the last known
            // values visible when only Salad.exe has been closed.
            return;
        }

        if (DateTimeOffset.Now - lastWalletRefresh < EstimateRefreshInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref walletRefreshRunning, 1, 0) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                RefreshWalletBalanceIfNeeded(true);
            }
            finally
            {
                Interlocked.Exchange(ref walletRefreshRunning, 0);
            }
        });
    }

    private static void RefreshWalletBalanceIfNeeded(bool saladRunning)
    {
        if (!saladRunning)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (now - lastWalletRefresh < EstimateRefreshInterval)
        {
            return;
        }

        lastWalletRefresh = now;

        WalletSnapshot snapshot;
        if (TryReadWalletSnapshotFromMonitorState(TimeSpan.FromSeconds(4), out snapshot))
        {
            previousWalletBalance = lastWalletBalance;
            previousWalletBalanceAt = lastWalletBalanceAt;
            lastWalletBalance = snapshot.CurrentBalance;
            lastWalletBalanceAt = now;
            lastWalletLast24Hours = snapshot.BalanceLast24Hours;
            if (previousWalletBalance.HasValue && previousWalletBalanceAt.HasValue)
            {
                lastWalletBalanceDelta = snapshot.CurrentBalance - previousWalletBalance.Value;
                lastWalletBalanceDeltaSeconds = (now - previousWalletBalanceAt.Value).TotalSeconds;
            }
            else
            {
                lastWalletBalanceDelta = null;
                lastWalletBalanceDeltaSeconds = null;
            }

            balanceText = FormatUsd(snapshot.CurrentBalance, false);
            last24HoursText = FormatUsd(snapshot.BalanceLast24Hours, true);
            Log(
                "wallet_refresh ok current=" +
                snapshot.CurrentBalance.ToString("0.########", CultureInfo.InvariantCulture) +
                " last24=" +
                snapshot.BalanceLast24Hours.ToString("0.########", CultureInfo.InvariantCulture));
        }
        else
        {
            balanceText = "?";
            last24HoursText = "?";
        }
    }

    private static string FormatUsd(double value, bool includePlus)
    {
        var prefix = includePlus && value > 0 ? "+$" : "$";
        if (includePlus && value < 0)
        {
            prefix = "-$";
            value = Math.Abs(value);
        }

        return prefix + value.ToString("0.0000", CultureInfo.InvariantCulture);
    }

    private static bool TryReadWalletSnapshotFromMonitorState(TimeSpan timeout, out WalletSnapshot snapshot)
    {
        snapshot = new WalletSnapshot();
        var path = "/salad.grpc.salad_bowl_widget.v1alpha.SaladBowlWidgetService/MonitorState";

        try
        {
            var port = GetSaladBowlGrpcPort();
            return TryReadGrpcHttp2MonitorStateWallet("127.0.0.1", port, path, timeout, out snapshot);
        }
        catch (Exception ex)
        {
            Log("wallet_refresh_error " + ex.Message);
            lastWalletRefresh = DateTimeOffset.Now - EstimateRefreshInterval + EstimateErrorRetryInterval;
            return false;
        }
    }

    private static bool TryReadGrpcHttp2MonitorStateWallet(
        string host,
        int port,
        string path,
        TimeSpan timeout,
        out WalletSnapshot snapshot)
    {
        snapshot = new WalletSnapshot();
        using (var client = new TcpClient())
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 1500;
            client.SendTimeout = 5000;
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

                var deadline = DateTimeOffset.Now + timeout;
                var pending = new byte[0];
                while (DateTimeOffset.Now < deadline)
                {
                    Http2Frame frame;
                    try
                    {
                        frame = ReadHttp2Frame(stream);
                    }
                    catch (IOException)
                    {
                        return false;
                    }

                    if (frame.Type == 0x4 && frame.StreamId == 0 && (frame.Flags & 0x1) == 0)
                    {
                        WriteHttp2Frame(stream, 0x4, 0x1, 0, new byte[0]);
                        continue;
                    }

                    if (frame.Type == 0x3 && frame.StreamId == 1)
                    {
                        Log("wallet_refresh_h2_reset");
                        return false;
                    }

                    if (frame.Type == 0x7)
                    {
                        Log("wallet_refresh_h2_goaway " + DecodeHttp2GoAway(frame.Payload));
                        return false;
                    }

                    if (frame.StreamId != 1)
                    {
                        continue;
                    }

                    if (frame.Type == 0x0 && frame.Payload.Length > 0)
                    {
                        AppendBytes(ref pending, frame.Payload);
                        if (TryConsumeGrpcMessagesForWallet(ref pending, out snapshot))
                        {
                            return true;
                        }
                    }

                    if ((frame.Flags & 0x1) != 0)
                    {
                        return false;
                    }
                }
            }
        }

        Log("wallet_refresh_timeout");
        return false;
    }

    private static void AppendBytes(ref byte[] pending, byte[] payload)
    {
        if (payload == null || payload.Length == 0)
        {
            return;
        }

        var combined = new byte[pending.Length + payload.Length];
        Buffer.BlockCopy(pending, 0, combined, 0, pending.Length);
        Buffer.BlockCopy(payload, 0, combined, pending.Length, payload.Length);
        pending = combined;
    }

    private static bool TryConsumeGrpcMessagesForWallet(ref byte[] pending, out WalletSnapshot snapshot)
    {
        snapshot = new WalletSnapshot();
        var offset = 0;
        while (offset + 5 <= pending.Length)
        {
            var length =
                (pending[offset + 1] << 24) |
                (pending[offset + 2] << 16) |
                (pending[offset + 3] << 8) |
                pending[offset + 4];
            if (length < 0 || offset + 5 + length > pending.Length)
            {
                break;
            }

            if (pending[offset] == 0)
            {
                var message = new byte[length];
                Buffer.BlockCopy(pending, offset + 5, message, 0, length);
                if (TryParseWalletFromMonitorStateResponse(message, out snapshot))
                {
                    return true;
                }
            }

            offset += 5 + length;
        }

        if (offset > 0)
        {
            var remaining = new byte[pending.Length - offset];
            Buffer.BlockCopy(pending, offset, remaining, 0, remaining.Length);
            pending = remaining;
        }

        return false;
    }

    private static bool TryParseWalletFromMonitorStateResponse(byte[] message, out WalletSnapshot snapshot)
    {
        snapshot = new WalletSnapshot();
        var index = 0;
        while (index < message.Length)
        {
            ulong tag;
            if (!TryReadProtoVarint(message, ref index, message.Length, out tag))
            {
                return false;
            }

            var field = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);
            if ((field == 5 || field == 100) && wireType == 2)
            {
                byte[] value;
                if (!TryReadProtoLengthDelimited(message, ref index, message.Length, out value))
                {
                    return false;
                }

                if (field == 5 && TryParseWalletMessage(value, out snapshot))
                {
                    return true;
                }

                if (field == 100 && TryParseWalletFromStateMessage(value, out snapshot))
                {
                    return true;
                }

                continue;
            }

            if (!SkipProtoField(message, ref index, message.Length, wireType))
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryParseWalletFromStateMessage(byte[] state, out WalletSnapshot snapshot)
    {
        snapshot = new WalletSnapshot();
        var index = 0;
        while (index < state.Length)
        {
            ulong tag;
            if (!TryReadProtoVarint(state, ref index, state.Length, out tag))
            {
                return false;
            }

            var field = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);
            if (field == 5 && wireType == 2)
            {
                byte[] wallet;
                if (!TryReadProtoLengthDelimited(state, ref index, state.Length, out wallet))
                {
                    return false;
                }

                return TryParseWalletMessage(wallet, out snapshot);
            }

            if (!SkipProtoField(state, ref index, state.Length, wireType))
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryParseWalletMessage(byte[] wallet, out WalletSnapshot snapshot)
    {
        snapshot = new WalletSnapshot();
        var index = 0;
        var sawWalletField = false;
        while (index < wallet.Length)
        {
            ulong tag;
            if (!TryReadProtoVarint(wallet, ref index, wallet.Length, out tag))
            {
                return false;
            }

            var field = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);
            if ((field == 1 || field == 2) && wireType == 1)
            {
                double value;
                if (!TryReadProtoDouble(wallet, ref index, wallet.Length, out value))
                {
                    return false;
                }

                sawWalletField = true;
                if (field == 1)
                {
                    snapshot.CurrentBalance = value;
                }
                else
                {
                    snapshot.BalanceLast24Hours = value;
                }

                continue;
            }

            if (!SkipProtoField(wallet, ref index, wallet.Length, wireType))
            {
                return false;
            }
        }

        return sawWalletField || wallet.Length == 0;
    }

    private struct WalletSnapshot
    {
        public double CurrentBalance;
        public double BalanceLast24Hours;
    }
}
