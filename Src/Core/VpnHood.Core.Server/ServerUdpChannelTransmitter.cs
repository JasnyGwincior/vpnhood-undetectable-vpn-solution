﻿using System.Net;
using System.Net.Sockets;
using VpnHood.Core.Tunneling.Channels;

namespace VpnHood.Core.Server;

public class ServerUdpChannelTransmitter(UdpClient udpClient, SessionManager sessionManager)
    : UdpChannelTransmitter(udpClient, sessionManager.ServerSecret)
{
    protected override void OnReceiveData(ulong sessionId, IPEndPoint remoteEndPoint,
        Memory<byte> buffer,
        long channelCryptorPosition)
    {
        var session = sessionManager.GetSessionById(sessionId) 
                      ?? throw new Exception($"Session does not found. SessionId: {sessionId}");

        //make sure UDP channel is added
        session.OnUdpTransmitterReceivedData(this, remoteEndPoint, channelCryptorPosition, buffer);
    }

    public static ServerUdpChannelTransmitter Create(IPEndPoint ipEndPoint, SessionManager sessionManager)
    {
        var udpClient = new UdpClient(ipEndPoint);
        try {
            return new ServerUdpChannelTransmitter(udpClient, sessionManager);
        }
        catch {
            udpClient.Dispose();
            throw;
        }
    }
}