﻿using VpnHood.Core.PacketTransports;

namespace VpnHood.Core.Tunneling.Proxies;

public interface IPacketProxyPool : IPacketTransport
{
    public int ClientCount { get; }
    public int RemoteEndPointCount { get; }
}