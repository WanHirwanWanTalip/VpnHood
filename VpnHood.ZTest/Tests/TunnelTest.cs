﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PacketDotNet;
using PacketDotNet.Utils;
using VpnHood.Common.Utils;
using VpnHood.Tunneling;
using VpnHood.Tunneling.Channels;
using VpnHood.Tunneling.Channels.Streams;
using ProtocolType = PacketDotNet.ProtocolType;

namespace VpnHood.Test.Tests;

[TestClass]
public class TunnelTest : TestBase
{
    private class PacketProxyReceiverTest : IPacketProxyReceiver
    {
        public int ReceivedCount { get; private set; }

        public Task OnPacketReceived(IPPacket packet)
        {
            lock (this)
                ReceivedCount++;
            return Task.CompletedTask;
        }

        public void OnNewRemoteEndPoint(ProtocolType protocolType, IPEndPoint remoteEndPoint)
        {
        }

        public void OnNewEndPoint(ProtocolType protocolType, IPEndPoint localEndPoint, IPEndPoint remoteEndPoint,
            bool isNewLocalEndPoint, bool isNewRemoteEndPoint)
        {
        }
    }

    [TestMethod]
    public async Task PingProxy_Pool()
    {
        // create icmp
        var packetReceiver = new PacketProxyReceiverTest();
        var payload = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };
        var buffer = new byte[4 + payload.Length];
        var icmpPacket = new IcmpV4Packet(new ByteArraySegment(buffer))
        {
            TypeCode = IcmpV4TypeCode.EchoRequest,
            Id = 1,
            Sequence = 1,
            PayloadData = payload
        };


        var ipPacket = PacketUtil.CreateIpPacket(IPAddress.Loopback, IPAddress.Parse("8.8.8.8"));
        ipPacket.PayloadPacket = icmpPacket;
        PacketUtil.UpdateIpPacket(ipPacket);

        using var pingProxyPool = new PingProxyPool(packetReceiver, maxClientCount: 3, icmpTimeout: null);
        var task1 = pingProxyPool.SendPacket(PacketUtil.ClonePacket(ipPacket));

        ipPacket = PacketUtil.CreateIpPacket(IPAddress.Parse("127.0.0.1"), IPAddress.Parse("127.0.0.2"));
        ipPacket.PayloadPacket = icmpPacket;
        icmpPacket.Sequence++;
        PacketUtil.UpdateIpPacket(ipPacket);
        var task2 = pingProxyPool.SendPacket(PacketUtil.ClonePacket(ipPacket));

        ipPacket = PacketUtil.CreateIpPacket(IPAddress.Parse("127.0.0.1"), IPAddress.Parse("127.0.0.2"));
        ipPacket.PayloadPacket = icmpPacket;
        icmpPacket.Sequence++;
        PacketUtil.UpdateIpPacket(ipPacket);
        var task3 = pingProxyPool.SendPacket(PacketUtil.ClonePacket(ipPacket));

        await Task.WhenAll(task1, task2, task3);
        Assert.AreEqual(3, packetReceiver.ReceivedCount);

        // let reuse
        await pingProxyPool.SendPacket(PacketUtil.ClonePacket(ipPacket));
        Assert.AreEqual(4, packetReceiver.ReceivedCount);
    }

    [TestMethod]
    public void UdpChannel_Direct()
    {
        EventWaitHandle waitHandle = new(true, EventResetMode.AutoReset);
        waitHandle.Reset();

        // test packets
        var packets = new List<IPPacket>
        {
            IPPacket.RandomPacket(IPVersion.IPv4),
            IPPacket.RandomPacket(IPVersion.IPv4),
            IPPacket.RandomPacket(IPVersion.IPv4)
        };

        // Create server
        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.GenerateKey();

        var serverUdpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serverUdpChannel = new UdpChannel(false, serverUdpClient, 200, aes.Key);
        serverUdpChannel.Start();

        var serverReceivedPackets = Array.Empty<IPPacket>();
        serverUdpChannel.OnPacketReceived += delegate (object? sender, ChannelPacketReceivedEventArgs e)
        {
            serverReceivedPackets = e.IpPackets.ToArray();
            _ = serverUdpChannel.SendPacket(e.IpPackets);
        };

        // Create client
        var clientUdpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        if (serverUdpClient.Client.LocalEndPoint == null)
            throw new Exception("Client connection has not been established!");

        clientUdpClient.Connect((IPEndPoint)serverUdpClient.Client.LocalEndPoint);
        var clientUdpChannel = new UdpChannel(true, clientUdpClient, 200, aes.Key);
        clientUdpChannel.Start();

        var clientReceivedPackets = Array.Empty<IPPacket>();
        clientUdpChannel.OnPacketReceived += delegate (object? _, ChannelPacketReceivedEventArgs e)
        {
            clientReceivedPackets = e.IpPackets.ToArray();
            waitHandle.Set();
        };

        // send packet to server through channel
        _ = clientUdpChannel.SendPacket(packets.ToArray());
        waitHandle.WaitOne(5000);
        Assert.AreEqual(packets.Count, serverReceivedPackets.Length);
        Assert.AreEqual(packets.Count, clientReceivedPackets.Length);
    }

    [TestMethod]
    public async Task UdpChannel_via_Tunnel()
    {
        var waitHandle = new EventWaitHandle(true, EventResetMode.AutoReset);
        waitHandle.Reset();

        // test packets
        var packets = new List<IPPacket>
        {
            IPPacket.RandomPacket(IPVersion.IPv4),
            IPPacket.RandomPacket(IPVersion.IPv4),
            IPPacket.RandomPacket(IPVersion.IPv4)
        };

        using var aes = Aes.Create();
        aes.KeySize = 128;
        aes.GenerateKey();

        // Create server
        var serverReceivedPackets = Array.Empty<IPPacket>();
        var serverUdpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var serverUdpChannel = new UdpChannel(false, serverUdpClient, 200, aes.Key);

        var serverTunnel = new Tunnel(new TunnelOptions());
        serverTunnel.AddChannel(serverUdpChannel);
        serverTunnel.OnPacketReceived += delegate (object? sender, ChannelPacketReceivedEventArgs e)
        {
            serverReceivedPackets = e.IpPackets.ToArray();
            _ = serverUdpChannel.SendPacket(e.IpPackets);
        };

        // Create client
        var clientReceivedPackets = Array.Empty<IPPacket>();
        var clientUdpClient = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        if (serverUdpClient.Client.LocalEndPoint == null)
            throw new Exception($"{nameof(serverUdpClient)} connection has not been established!");
        clientUdpClient.Connect((IPEndPoint)serverUdpClient.Client.LocalEndPoint);
        var clientUdpChannel = new UdpChannel(true, clientUdpClient, 200, aes.Key);

        var clientTunnel = new Tunnel();
        clientTunnel.AddChannel(clientUdpChannel);
        clientTunnel.OnPacketReceived += delegate (object? _, ChannelPacketReceivedEventArgs e)
        {
            clientReceivedPackets = e.IpPackets.ToArray();
            waitHandle.Set();
        };

        // send packet to server through tunnel
        await clientTunnel.SendPackets(packets.ToArray());
        await VhTestUtil.AssertEqualsWait(packets.Count, () => serverReceivedPackets.Length);
        await VhTestUtil.AssertEqualsWait(packets.Count, () => clientReceivedPackets.Length);
    }

    private static async Task SimpleLoopback(TcpListener tcpListener, CancellationToken cancellationToken)
    {
        using var client = await tcpListener.AcceptTcpClientAsync(cancellationToken);
        
        // Create a memory stream to store the incoming data
        ChunkStream binaryStream = new BinaryStream(client.GetStream(), Guid.NewGuid().ToString(), new byte[16]);
        while (true)
        {
            using var memoryStream = new MemoryStream();
            // Read the request data from the client to memoryStream
            var buffer = new byte[2048];
            var bytesRead = await binaryStream.ReadAsync(buffer, 0, 4, cancellationToken);
            if (bytesRead == 0) 
                break;
            
            if (bytesRead != 4) 
                throw new Exception("Unexpected data.");

            var length = BitConverter.ToInt32(buffer, 0);
            var totalRead = 0;
            while (totalRead != length)
            {
                bytesRead = await binaryStream.ReadAsync(buffer, 0, 120, cancellationToken);
                if (bytesRead == 0) throw new Exception("Unexpected data");
                totalRead += bytesRead;
                memoryStream.Write(buffer, 0, bytesRead);
            }

            await binaryStream.WriteAsync(memoryStream.ToArray(), cancellationToken);

            if (!binaryStream.CanReuse)
                break;

            binaryStream = await binaryStream.CreateReuse();
        }

        Assert.IsFalse(binaryStream.CanReuse);
    }

    [TestMethod]
    public async Task ChunkStream()
    {
        // create server
        var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        tcpListener.Start();
        var workerTask = SimpleLoopback(tcpListener, cts.Token);

        // connect to server
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync((IPEndPoint)tcpListener.LocalEndpoint, cts.Token);
        var stream = tcpClient.GetStream();

        // send data
        var chunks = new List<string>
        {
            "HelloHelloHelloHelloHelloHelloHelloHello\r\n",
            "Apple1234,\r\nApple1234,Apple1234,Apple1234,Apple1234,Apple1234,Apple1234,Apple1234,Apple1234,Apple1234",
            "Book009,Book009,Book009,Book009,Book009,Book009,Book009,Book009,Book009,Book009",
            "550Clock\n\r,550Clock,550Clock,550Clock,550Clock,550Clock,550Clock,550Clock,550Clock,550Clock"
        };

        // first stream
        ChunkStream binaryStream = new BinaryStream(stream, Guid.NewGuid().ToString(), new byte[16]);
        await binaryStream.WriteAsync(BitConverter.GetBytes(chunks.Sum(x => x.Length)), cts.Token);
        foreach (var chunk in chunks)
            await binaryStream.WriteAsync(Encoding.UTF8.GetBytes(chunk).ToArray(), cts.Token);
        Assert.AreEqual(chunks.Count + 1, binaryStream.WroteChunkCount);

        // read first stream
        var sr = new StreamReader(binaryStream, bufferSize: 10);
        var res = await sr.ReadToEndAsync(cts.Token);
        Assert.AreEqual(string.Join("", chunks), res);

        // write second stream
        binaryStream = (BinaryStream)await binaryStream.CreateReuse();
        await binaryStream.WriteAsync(BitConverter.GetBytes(chunks.Sum(x => x.Length)).ToArray(), cts.Token);
        foreach (var chunk in chunks)
            await binaryStream.WriteAsync(Encoding.UTF8.GetBytes(chunk).ToArray(), cts.Token);
        Assert.AreEqual(chunks.Count + 1, binaryStream.WroteChunkCount);

        // read second stream
        sr = new StreamReader(binaryStream, bufferSize: 10);
        res = await sr.ReadToEndAsync(cts.Token);
        Assert.AreEqual(string.Join("", chunks), res);

        await binaryStream.DisposeAsync();
        tcpClient.Dispose();

        // task must completed after binaryStream.DisposeAsync
        await workerTask.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
        Assert.IsTrue(workerTask.IsCompletedSuccessfully);

        tcpListener.Stop();
        cts.Cancel();
    }

    [TestMethod]
    public async Task ChunkStream_Binary()
    {
        // create server
        var tcpListener = new TcpListener(IPAddress.Loopback, 0);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        tcpListener.Start();
        _ = SimpleLoopback(tcpListener, cts.Token);

        // connect to server
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync((IPEndPoint)tcpListener.LocalEndpoint, cts.Token);
        var stream = tcpClient.GetStream();

        // send data
        var writeBuffer = new byte[10 * 1024 * 1024 + 2000]; // 10MB buffer size
        var random = new Random();
        random.NextBytes(writeBuffer);

        // write stream
        ChunkStream binaryStream = new BinaryStream(stream, Guid.NewGuid().ToString(), new byte[16]);
        await binaryStream.WriteAsync(BitConverter.GetBytes(writeBuffer.Length), cts.Token);
        await binaryStream.WriteAsync((byte[])writeBuffer.Clone(), cts.Token);

        // read stream
        var readBuffer = new byte[writeBuffer.Length];
        await binaryStream.ReadExactlyAsync(readBuffer, cts.Token);
        CollectionAssert.AreEqual(writeBuffer, readBuffer);

        Assert.AreEqual(0, await binaryStream.ReadAsync(readBuffer, cts.Token));
        await binaryStream.DisposeAsync();

        tcpListener.Stop();
        cts.Cancel();
    }
}