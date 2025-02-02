﻿using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using VpnHood.Common.Logging;

namespace VpnHood.Tunneling.Channels;

public abstract class UdpChannelTransmitter : IDisposable
{
    private readonly UdpClient _udpClient;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly BufferCryptor _serverCryptor;
    private readonly RNGCryptoServiceProvider _randomGenerator = new();
    private readonly byte[] _sendIv = new byte[8];
    public const int HeaderLength = 32; //IV (8) + Sign (2) + Reserved (6) + SessionId (8) + SessionPos (8)
    private readonly byte[] _sendHeadKeyBuffer = new byte[HeaderLength - 8]; // IV will not be encrypted
    private bool _disposed;

    protected UdpChannelTransmitter(UdpClient udpClient, byte[] serverKey)
    {
        _udpClient = udpClient;
        _serverCryptor = new BufferCryptor(serverKey);
        _ = ReadTask();
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)_udpClient.Client.LocalEndPoint;

    public async Task<int> SendAsync(IPEndPoint? ipEndPoint, ulong sessionId, long sessionCryptoPosition,
        byte[] buffer, int bufferLength, int protocolVersion)
    {
        try
        {
            await _semaphore.WaitAsync();

            // add random packet iv
            _randomGenerator.GetBytes(_sendIv);
            _sendIv.CopyTo(buffer, 0);

            buffer[8] = (byte)'O';
            buffer[9] = (byte)'K';
            BitConverter.GetBytes(sessionId).CopyTo(buffer, 16);
            BitConverter.GetBytes(sessionCryptoPosition).CopyTo(buffer, 24);

            // encrypt session info part. It encrypt the session number and counter by server key and perform bitwise XOR on head.
            // the data is not important but it remove the signature of our packet by obfuscating the packet head. Also, the counter of session 
            // never repeats so ECB generate a new key each time. It is just for the head obfuscation and the reset of data will encrypt
            // by session key and that counter
            Array.Clear(_sendHeadKeyBuffer, 0, _sendHeadKeyBuffer.Length);
            _serverCryptor.Cipher(_sendHeadKeyBuffer, 0, _sendHeadKeyBuffer.Length, BitConverter.ToInt64(_sendIv));
            for (var i = 0; i < _sendHeadKeyBuffer.Length; i++)
                buffer[_sendIv.Length + i] ^= _sendHeadKeyBuffer[i]; //simple XOR with generated unique key

            var ret = ipEndPoint != null
                ? await _udpClient.SendAsync(buffer, bufferLength, ipEndPoint)
                : await _udpClient.SendAsync(buffer, bufferLength);

            if (ret != bufferLength)
                throw new Exception($"UdpClient: Send {ret} bytes instead {buffer.Length} bytes.");

            return ret;
        }
        catch (Exception ex)
        {
            if (IsInvalidState(ex))
                Dispose();

            VhLogger.Instance.LogError(GeneralEventId.Udp, ex,
                "UdpChannelTransmitter: Could not send data. DataLength: {DataLength}, DestinationIp: {DestinationIp}",
                buffer.Length, VhLogger.Format(ipEndPoint));
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task ReadTask()
    {
        var headKeyBuffer = new byte[_sendHeadKeyBuffer.Length];

        // wait for all incoming UDP packets
        while (!_disposed)
        {
            var receiving = true;
            try
            {
                var udpResult = await _udpClient.ReceiveAsync();
                receiving = false;
                var buffer = udpResult.Buffer;
                if (buffer.Length < HeaderLength)
                    throw new Exception("Invalid UDP packet size. Could not find its header.");

                // build header key
                var bufferIndex = 0;
                var iv = BitConverter.ToInt64(buffer, 0);
                Array.Clear(headKeyBuffer, 0, headKeyBuffer.Length);
                _serverCryptor.Cipher(headKeyBuffer, 0, headKeyBuffer.Length, iv);
                bufferIndex += 8;

                // decrypt header
                for (var i = 0; i < headKeyBuffer.Length; i++)
                    buffer[bufferIndex + i] ^= headKeyBuffer[i]; //simple XOR with the generated unique key

                // check packet signature OK
                if (buffer[8] != 'O' || buffer[9] != 'K') 
                    throw new Exception("Packet signature does not match.");

                // read session and session position
                bufferIndex += 8;
                var sessionId = BitConverter.ToUInt64(buffer, bufferIndex);
                bufferIndex += 8;
                var channelCryptorPosition = BitConverter.ToInt64(buffer, bufferIndex);
                bufferIndex += 8;

                OnReceiveData(sessionId, udpResult.RemoteEndPoint, channelCryptorPosition, udpResult.Buffer, bufferIndex);
            }
            catch (Exception ex)
            {
                if (IsInvalidState(ex) && receiving)
                    Dispose();
                else
                    VhLogger.Instance.LogWarning(GeneralEventId.Udp,
                        "Error in receiving UDP packets. Exception: {Message}", ex.Message);
            }
        }

        Dispose();
    }

    protected abstract void OnReceiveData(ulong sessionId, IPEndPoint remoteEndPoint, long channelCryptorPosition,
        byte[] buffer, int bufferIndex);

    private bool IsInvalidState(Exception ex)
    {
        return
            _disposed ||
            ex is ObjectDisposedException or SocketException { SocketErrorCode: SocketError.InvalidArgument };
    }

    public void Dispose()
    {
        _disposed = true;
        _udpClient.Dispose();
    }
}