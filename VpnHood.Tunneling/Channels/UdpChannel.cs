﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PacketDotNet;
using VpnHood.Common.Logging;
using VpnHood.Common.Messaging;
using VpnHood.Common.Utils;

namespace VpnHood.Tunneling.Channels;

// todo: deprecated version >= 2.9.362
public class UdpChannel : IDatagramChannel
{
    private readonly byte[] _buffer = new byte[0xFFFF];
    private readonly BufferCryptor _bufferCryptor;
    private readonly int _bufferHeaderLength;
    private readonly long _cryptorPosBase;
    private readonly bool _isClient;
    private readonly uint _sessionId;
    private readonly UdpClient _udpClient;
    private bool _disposed;
    private IPEndPoint? _lastRemoteEp;

    public string ChannelId { get; } = Guid.NewGuid().ToString();
    public byte[] Key { get; }
    public int LocalPort => ((IPEndPoint)_udpClient.Client.LocalEndPoint).Port;
    public bool Connected { get; private set; }
    public Traffic Traffic { get; } = new();
    public DateTime LastActivityTime { get; private set; }
    public event EventHandler<ChannelPacketReceivedEventArgs>? OnPacketReceived;

    public UdpChannel(bool isClient, UdpClient udpClient, ulong sessionId, byte[] key)
    {
        VhLogger.Instance.LogInformation(GeneralEventId.Udp, $"Creating a {nameof(UdpChannel)}. SessionId: {VhLogger.FormatId(_sessionId)} ...");

        Key = key;
        _isClient = isClient;
        _cryptorPosBase = isClient ? 0 : long.MaxValue / 2;
        _bufferCryptor = new BufferCryptor(key);
        _sessionId = (uint)sessionId; // legacy
        _udpClient = udpClient;
        _bufferHeaderLength = _isClient
            ? 4 + 8 // client->server: sessionId + sentBytes (IV)
            : 8; // server->client: sentBytes(IV)

        //tunnel manages fragmentation; we just need to send it as possible
        if (udpClient.Client.AddressFamily == AddressFamily.InterNetwork)
            udpClient.DontFragment = false; // Never call this for IPv6, it will throw exception for any value

    }

    public void Start()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(StreamDatagramChannel));

        if (Connected)
            throw new Exception("Start has already been called!");

        Connected = true;
        _ = ReadTask();
    }

    public async Task SendPacket(IPPacket[] ipPackets)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(StreamDatagramChannel));

        // Tunnel optimizes the packets in regard of MTU without fragmentation 
        // so here we are not worry about optimizing it and can use fragmentation because the sum of 
        // packets should be small enough to fill a udp packet
        var maxDataLen = TunnelDefaults.MtuWithFragmentation - _bufferHeaderLength;
        var dataLen = ipPackets.Sum(x => x.TotalPacketLength);
        if (dataLen > maxDataLen)
            throw new InvalidOperationException(
                $"Total packets length is too big for {VhLogger.FormatType(this)}. MaxSize: {maxDataLen}, Packets Size: {dataLen} !");

        // copy packets to buffer
        var buffer = _buffer;
        var bufferIndex = _bufferHeaderLength;

        // add sessionId for verification
        BitConverter.GetBytes(_sessionId).CopyTo(buffer, bufferIndex);
        bufferIndex += 4;
        foreach (var ipPacket in ipPackets)
        {
            Buffer.BlockCopy(ipPacket.Bytes, 0, buffer, bufferIndex, ipPacket.TotalPacketLength);
            bufferIndex += ipPacket.TotalPacketLength;
        }

        await Send(buffer, bufferIndex);
    }

    private async Task ReadTask()
    {
        var ipPackets = new List<IPPacket>();

        // wait for all incoming UDP packets
        while (!_disposed)
        {
            try
            {
                var udpResult = await _udpClient.ReceiveAsync();
                _lastRemoteEp = udpResult.RemoteEndPoint;
                var buffer = udpResult.Buffer;

                // decrypt buffer
                var bufferIndex = 0;
                if (_isClient)
                {
                    var cryptoPos = BitConverter.ToInt64(buffer, bufferIndex);
                    bufferIndex += 8;
                    _bufferCryptor.CipherOld(buffer, bufferIndex, buffer.Length, cryptoPos);
                }
                else
                {
                    var sessionId = BitConverter.ToUInt32(buffer, bufferIndex);
                    bufferIndex += 4;
                    if (sessionId != _sessionId)
                        throw new UnauthorizedAccessException("Invalid sessionId");

                    var cryptoPos = BitConverter.ToInt64(buffer, bufferIndex);
                    bufferIndex += 8;
                    _bufferCryptor.CipherOld(buffer, bufferIndex, buffer.Length, cryptoPos);
                }

                // verify sessionId after cipher
                var sessionId2 = BitConverter.ToUInt32(buffer, bufferIndex);
                bufferIndex += 4;
                if (sessionId2 != _sessionId)
                    throw new UnauthorizedAccessException("Invalid sessionId");

                // read all packets
                while (bufferIndex < buffer.Length)
                {
                    var ipPacket = PacketUtil.ReadNextPacket(buffer, ref bufferIndex);
                    Traffic.Received += ipPacket.TotalPacketLength;
                    ipPackets.Add(ipPacket);
                }
            }
            catch (Exception ex)
            {
                if (IsInvalidState(ex))
                    await DisposeAsync();
                else
                    VhLogger.Instance.LogWarning(GeneralEventId.Udp,
                        $"Error in receiving packets. Exception: {ex.Message}");
            }

            // send collected packets when there is no more packets in the UdpClient buffer
            if (!_disposed && _udpClient.Available == 0)
            {
                FireReceivedPackets(ipPackets.ToArray());
                ipPackets.Clear();
            }
        }

        await DisposeAsync();
    }

    private void FireReceivedPackets(IPPacket[] ipPackets)
    {
        if (_disposed)
            return;

        try
        {
            OnPacketReceived?.Invoke(this, new ChannelPacketReceivedEventArgs(ipPackets, this));
            LastActivityTime = FastDateTime.Now;
        }
        catch (Exception ex)
        {
            VhLogger.Instance.LogWarning(GeneralEventId.Udp,
                $"Error in processing received packets. Exception: {ex.Message}");
        }
    }

    private async Task Send(byte[] buffer, int bufferCount)
    {
        try
        {
            int ret;
            if (VhLogger.IsDiagnoseMode)
                VhLogger.Instance.Log(LogLevel.Information, GeneralEventId.Udp,
                    $"{VhLogger.FormatType(this)} is sending {bufferCount} bytes...");

            var cryptoPos = _cryptorPosBase + Traffic.Sent;
            _bufferCryptor.CipherOld(buffer, _bufferHeaderLength, bufferCount, cryptoPos);
            if (_isClient)
            {
                BitConverter.GetBytes(_sessionId).CopyTo(buffer, 0);
                BitConverter.GetBytes(cryptoPos).CopyTo(buffer, 4);
                ret = await _udpClient.SendAsync(buffer, bufferCount);
            }
            else
            {
                BitConverter.GetBytes(cryptoPos).CopyTo(buffer, 0);
                ret = await _udpClient.SendAsync(buffer, bufferCount, _lastRemoteEp);
            }

            if (ret != bufferCount)
                throw new Exception(
                    $"{VhLogger.FormatType(this)}: Send {ret} bytes instead {bufferCount} bytes! ");

            Traffic.Sent += ret;
            LastActivityTime = FastDateTime.Now;
        }
        catch (Exception ex)
        {
            VhLogger.Instance.Log(LogLevel.Error, GeneralEventId.Udp,
                $"{VhLogger.FormatType(this)}: Could not send {bufferCount} packets! Message: {ex.Message}");
            if (IsInvalidState(ex))
                await DisposeAsync();
        }
    }

    private bool IsInvalidState(Exception ex)
    {
        return _disposed || ex is ObjectDisposedException or SocketException { SocketErrorCode: SocketError.InvalidArgument };
    }

    public ValueTask DisposeAsync(bool graceFul)
    {
        _ = graceFul;
        return DisposeAsync();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return default;
        _disposed = true;

        VhLogger.Instance.LogInformation(GeneralEventId.Udp,
            $"Disposing a {nameof(UdpChannel)}. SessionId: {VhLogger.FormatId(_sessionId)} ...");

        Connected = false;
        _bufferCryptor.Dispose();
        _udpClient.Dispose();

        return default;
    }
}