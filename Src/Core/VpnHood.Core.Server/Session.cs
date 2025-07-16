﻿using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using VpnHood.Core.Common.Messaging;
using VpnHood.Core.Packets;
using VpnHood.Core.Packets.Extensions;
using VpnHood.Core.Server.Abstractions;
using VpnHood.Core.Server.Access.Configurations;
using VpnHood.Core.Server.Access.Managers;
using VpnHood.Core.Server.Access.Messaging;
using VpnHood.Core.Server.Exceptions;
using VpnHood.Core.Server.Utils;
using VpnHood.Core.Toolkit.Logging;
using VpnHood.Core.Toolkit.Utils;
using VpnHood.Core.Tunneling;
using VpnHood.Core.Tunneling.Channels;
using VpnHood.Core.Tunneling.ClientStreams;
using VpnHood.Core.Tunneling.Exceptions;
using VpnHood.Core.Tunneling.Messaging;
using VpnHood.Core.Tunneling.Proxies;
using VpnHood.Core.Tunneling.Sockets;
using VpnHood.Core.Tunneling.Utils;
using VpnHood.Core.VpnAdapters.Abstractions;

namespace VpnHood.Core.Server;

public class Session : IDisposable
{
    private readonly INetFilter _netFilter;
    private readonly IAccessManager _accessManager;
    private readonly IVpnAdapter? _vpnAdapter;
    private readonly ISocketFactory _socketFactory;
    private readonly ProxyManager _proxyManager;
    private readonly object _verifyRequestLock = new();
    private readonly int _maxTcpConnectWaitCount;
    private readonly int _maxTcpChannelCount;
    private readonly int? _tcpBufferSize;
    private readonly int? _tcpKernelSendBufferSize;
    private readonly int? _tcpKernelReceiveBufferSize;
    private readonly TrackingOptions _trackingOptions;
    private UdpChannel? _udpChannel;

    private readonly EventReporter _netScanExceptionReporter = new(
        "NetScan protector does not allow this request.", GeneralEventId.NetProtect);

    private readonly EventReporter _maxTcpChannelExceptionReporter = new(
        "Maximum TcpChannel has been reached.", GeneralEventId.NetProtect);

    private readonly EventReporter _maxTcpConnectWaitExceptionReporter = new(
        "Maximum TcpConnectWait has been reached.", GeneralEventId.NetProtect);

    private readonly EventReporter _filterReporter = new("Some requests have been blocked.", 
        GeneralEventId.NetProtect);

    private Traffic _prevTraffic = new();
    private int _tcpConnectWaitCount;

    public Tunnel Tunnel { get; }
    public ulong SessionId { get; }
    public byte[] SessionKey { get; }
    public SessionResponse SessionResponseEx { get; internal set; }
    public bool IsDisposed => DisposedTime != null;
    public DateTime? DisposedTime { get; private set; }
    public NetScanDetector? NetScanDetector { get; }
    public SessionExtraData ExtraData { get; }
    public int ProtocolVersion { get; }
    public int TcpConnectWaitCount => _tcpConnectWaitCount;
    public int TcpChannelCount => Tunnel.StreamProxyChannelCount + (_udpChannel != null ? 0 : Tunnel.PacketChannelCount);
    public int UdpConnectionCount => _proxyManager.UdpClientCount;
    public DateTime LastActivityTime => Tunnel.LastActivityTime;
    public VirtualIpBundle VirtualIps { get; }

    internal Session(IAccessManager accessManager,
        IVpnAdapter? vpnAdapter,
        INetFilter netFilter,
        ISocketFactory socketFactory,
        SessionResponseEx sessionResponseEx,
        SessionOptions options,
        TrackingOptions trackingOptions,
        SessionExtraData extraData,
        VirtualIpBundle virtualIps)
    {
        var sessionTuple = Tuple.Create("SessionId", (object?)sessionResponseEx.SessionId);
        var logScope = new LogScope();
        logScope.Data.Add(sessionTuple);

        _accessManager = accessManager ?? throw new ArgumentNullException(nameof(accessManager));
        _vpnAdapter = vpnAdapter;
        _socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
        _proxyManager = new ProxyManager(socketFactory, new ProxyManagerOptions {
            UdpTimeout = options.UdpTimeoutValue,
            IcmpTimeout = options.IcmpTimeoutValue,
            MaxUdpClientCount = options.MaxUdpClientCountValue,
            MaxPingClientCount = options.MaxIcmpClientCountValue,
            UdpReceiveBufferSize = options.UdpProxyReceiveBufferSize ?? TunnelDefaults.ClientUdpReceiveBufferSize,
            UdpSendBufferSize = options.UdpProxySendBufferSize,
            LogScope = logScope,
            IsPingSupported = true,
            PacketProxyCallbacks = new PacketProxyCallbacks(this),
            AutoDisposePackets = true,
            PacketQueueCapacity = TunnelDefaults.ProxyPacketQueueCapacity,
            UseUdpProxy2 = options.UseUdpProxy2Value
        });
        _proxyManager.PacketReceived += Proxy_PacketsReceived;
        _trackingOptions = trackingOptions;
        _maxTcpConnectWaitCount = options.MaxTcpConnectWaitCountValue;
        _maxTcpChannelCount = options.MaxTcpChannelCountValue;
        _tcpBufferSize = options.TcpBufferSize;
        _tcpKernelSendBufferSize = options.TcpKernelSendBufferSize;
        _tcpKernelReceiveBufferSize = options.TcpKernelReceiveBufferSize;
        _netFilter = netFilter;
        _netScanExceptionReporter.LogScope.Data.AddRange(logScope.Data);
        _maxTcpConnectWaitExceptionReporter.LogScope.Data.AddRange(logScope.Data);
        _maxTcpChannelExceptionReporter.LogScope.Data.AddRange(logScope.Data);
        ExtraData = extraData;
        VirtualIps = virtualIps;
        SessionResponseEx = sessionResponseEx;
        ProtocolVersion = sessionResponseEx.ProtocolVersion;
        SessionId = sessionResponseEx.SessionId;
        SessionKey = sessionResponseEx.SessionKey ?? throw new InvalidOperationException(
            $"{nameof(sessionResponseEx)} does not have {nameof(sessionResponseEx.SessionKey)}!");
        Tunnel = new Tunnel(new TunnelOptions {
            MaxPacketChannelCount = options.MaxPacketChannelCountValue,
            PacketQueueCapacity = TunnelDefaults.TunnelPacketQueueCapacity,
            AutoDisposePackets = true
        });
        Tunnel.PacketReceived += Tunnel_PacketReceived;

        // ReSharper disable once MergeIntoPattern
        if (options.NetScanLimit != null && options.NetScanTimeout != null)
            NetScanDetector = new NetScanDetector(options.NetScanLimit.Value, options.NetScanTimeout.Value);
    }

    public Traffic Traffic {
        get {
            // Intentionally Reversed: sending to tunnel means receiving form client,
            // Intentionally Reversed: receiving from tunnel means sending for client
            var traffic = Tunnel.Traffic - _prevTraffic;
            return new Traffic {
                Sent = traffic.Received,
                Received = traffic.Sent
            };
        }
    }

    public Traffic ResetTraffic()
    {
        var traffic = Traffic;
        _prevTraffic = Tunnel.Traffic;
        return traffic;
    }

    public void SetSyncRequired() => IsSyncRequired = true;
    public bool IsSyncRequired { get; private set; }

    public bool ResetSyncRequired()
    {
        var oldValue = IsSyncRequired;
        IsSyncRequired = false;
        return oldValue;
    }

    public void OnUdpTransmitterReceivedData(UdpChannelTransmitter transmitter, IPEndPoint remoteEndPoint,
        long cryptorPosition, Memory<byte> buffer)
    {
        // create and add udp channel if not exists
        if (_udpChannel is not { State: PacketChannelState.Connected }) {
            _udpChannel = TryPrepareUdpChannel(transmitter, remoteEndPoint);
            if (_udpChannel == null)
                return;
        }

        // set remote end point and notify channel about data received
        _udpChannel.RemoteEndPoint = remoteEndPoint;
        _udpChannel.OnDataReceived(buffer, cryptorPosition);
    }

    private UdpChannel? TryPrepareUdpChannel(UdpChannelTransmitter transmitter, IPEndPoint remoteEndPoint)
    {
        // add the new udp channel
        UdpChannel? udpChannel = null;
        try {
            // add new channel
            udpChannel = new UdpChannel(transmitter, new UdpChannelOptions {
                RemoteEndPoint = remoteEndPoint,
                Blocking = false,
                SessionId = SessionId,
                SessionKey = SessionKey,
                LeaveTransmitterOpen = true,
                AutoDisposePackets = true,
                ProtocolVersion = ProtocolVersion,
                Lifespan = null,
                ChannelId = Guid.NewGuid().ToString()
            });

            // remove old channels
            Tunnel.RemoveAllPacketChannels();
            Tunnel.AddChannel(udpChannel);
            return udpChannel;
        }
        catch (Exception ex) {
            VhLogger.Instance.LogError(GeneralEventId.PacketChannel, ex,
                "Failed to create UdpChannel for session {SessionId}", SessionId);

            udpChannel?.Dispose();
            return null;
        }
    }

    private IPAddress GetClientVirtualIp(IpVersion ipVersion)
    {
        return ipVersion == IpVersion.IPv4 ? VirtualIps.IpV4 : VirtualIps.IpV6;
    }

    private void Proxy_PacketsReceived(object? sender, IpPacket ipPacket)
    {
        Proxy_PacketReceived(ipPacket);
    }

    public void Adapter_PacketReceived(object? sender, IpPacket ipPacket)
    {
        Proxy_PacketReceived(ipPacket);
    }

    private void Proxy_PacketReceived(IpPacket ipPacket)
    {
        if (IsDisposed) 
            return;
        
        PacketLogger.LogPacket(ipPacket, "Delegating a packet to client...");
        ipPacket = _netFilter.ProcessReply(ipPacket);
        Tunnel.SendPacketQueued(ipPacket); // PacketEnqueue will dispose packets
    }

    private void Tunnel_PacketReceived(object? sender, IpPacket ipPacket)
    {
        if (IsDisposed)
            return;

        // filter requests
        var virtualIp = GetClientVirtualIp(ipPacket.Version);

        // reject if packet source does not match client internal ip
        if (!ipPacket.SourceAddress.Equals(virtualIp)) {
            var ipeEndPointPair = ipPacket.GetEndPoints();
            LogTrack(ipPacket.Protocol, null, ipeEndPointPair.RemoteEndPoint, false, true, "NetFilter");
            _filterReporter.Raise();
            throw new NetFilterException($"Invalid tunnel packet source ip. SourceIp: {VhLogger.Format(ipPacket.SourceAddress)}");
        }

        // filter
        var ipPacket2 = _netFilter.ProcessRequest(ipPacket);
        if (ipPacket2 == null) {
            var ipeEndPointPair = ipPacket.GetEndPoints();
            LogTrack(ipPacket.Protocol, null, ipeEndPointPair.RemoteEndPoint, false, true, "NetFilter");
            _filterReporter.Raise();
            throw new NetFilterException($"Packet discarded due to the NetFilter's policies. DestinationIp: {VhLogger.Format(ipPacket.DestinationAddress)}");
        }

        // send using tunnel or proxy
        if (_vpnAdapter?.IsIpVersionSupported(ipPacket2.Version) == true)
            _vpnAdapter.SendPacketQueued(ipPacket2);
        else
            _proxyManager.SendPacketQueued(ipPacket2);
    }

    public void LogTrack(IpProtocol protocol, IPEndPoint? localEndPoint, IPEndPoint? destinationEndPoint,
        bool isNewLocal, bool isNewRemote, string? failReason)
    {
        if (!_trackingOptions.IsEnabled)
            return;

        if (_trackingOptions is { TrackDestinationIpValue: false, TrackDestinationPortValue: false } && !isNewLocal &&
            failReason == null)
            return;

        if (!_trackingOptions.TrackTcpValue && protocol is IpProtocol.Tcp ||
            !_trackingOptions.TrackUdpValue && protocol is IpProtocol.Udp ||
            !_trackingOptions.TrackUdpValue && protocol is IpProtocol.IcmpV4 or IpProtocol.IcmpV6)
            return;

        var mode = (isNewLocal ? "L" : "") + (isNewRemote ? "R" : "");
        var localPortStr = "-";
        var destinationIpStr = "-";
        var destinationPortStr = "-";
        var netScanCount = "-";
        failReason ??= "Ok";

        if (localEndPoint != null)
            localPortStr = _trackingOptions.TrackLocalPortValue ? localEndPoint.Port.ToString() : "*";

        if (destinationEndPoint != null) {
            destinationIpStr = _trackingOptions.TrackDestinationIpValue
                ? VhUtils.RedactIpAddress(destinationEndPoint.Address)
                : "*";
            destinationPortStr = _trackingOptions.TrackDestinationPortValue ? destinationEndPoint.Port.ToString() : "*";
            netScanCount = NetScanDetector?.GetBurstCount(destinationEndPoint).ToString() ?? "*";
        }

        VhLogger.Instance.LogInformation(GeneralEventId.Track,
            "{Proto,-4}\tSessionId {SessionId}\t{Mode,-2}\tTcpCount {TcpCount,4}\tUdpCount {UdpCount,4}\tTcpWait {TcpConnectWaitCount,3}\tNetScan {NetScan,3}\t" +
            "SrcPort {SrcPort,-5}\tDstIp {DstIp,-15}\tDstPort {DstPort,-5}\t{Success,-10}",
            protocol, SessionId, mode,
            TcpChannelCount, _proxyManager.UdpClientCount, _tcpConnectWaitCount, netScanCount,
            localPortStr, destinationIpStr, destinationPortStr, failReason);
    }

    public async Task ProcessTcpPacketChannelRequest(TcpPacketChannelRequest request, IClientStream clientStream,
        CancellationToken cancellationToken)
    {
        // send OK reply
        await clientStream.WriteResponseAsync(SessionResponseEx, cancellationToken).Vhc();

        // Disable UdpChannel
        if (_udpChannel != null) {
            Tunnel.RemoveAllPacketChannels();
            _udpChannel = null;
        }
       
        // add channel
        VhLogger.Instance.LogDebug(GeneralEventId.PacketChannel,
            "Creating a TcpPacketChannel channel. SessionId: {SessionId}", VhLogger.FormatSessionId(SessionId));

        var channel = new StreamPacketChannel(new StreamPacketChannelOptions {
            Blocking = false,
            AutoDisposePackets = true,
            ClientStream = clientStream,
            ChannelId = request.RequestId,
            Lifespan = null
        });

        try {
            Tunnel.AddChannel(channel);
        }
        catch {
            channel.Dispose();
            throw;
        }
    }

    public Task ProcessUdpPacketRequest(UdpPacketRequest request, IClientStream clientStream,
        CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public async Task ProcessSessionStatusRequest(SessionStatusRequest request, IClientStream clientStream,
        CancellationToken cancellationToken)
    {
        await clientStream.DisposeAsync(SessionResponseEx, cancellationToken).Vhc();
    }

    public async Task ProcessRewardedAdRequest(RewardedAdRequest request, IClientStream clientStream,
        CancellationToken cancellationToken)
    {
        SessionResponseEx = await _accessManager
            .Session_AddUsage(sessionId: SessionId, new Traffic(), adData: request.AdData).Vhc();
        await clientStream.DisposeAsync(SessionResponseEx, cancellationToken).Vhc();
    }

    public async Task ProcessTcpProxyRequest(StreamProxyChannelRequest request, IClientStream clientStream,
        CancellationToken cancellationToken)
    {
        var isRequestedEpException = false;
        var isTcpConnectIncreased = false;

        TcpClient? tcpClientHost = null;
        TcpClientStream? tcpClientStreamHost = null;
        ProxyChannel? proxyChannel = null;
        try {
            // connect to requested site
            VhLogger.Instance.LogDebug(GeneralEventId.ProxyChannel,
                "Connecting to the requested endpoint. RequestedEP: {Format}", VhLogger.Format(request.DestinationEndPoint));

            // Apply limitation and update endpoint if needed
            VerifyTcpChannelRequest(clientStream, request);

            // prepare client
            Interlocked.Increment(ref _tcpConnectWaitCount);
            isTcpConnectIncreased = true;

            //set reuseAddress to  true to prevent error only one usage of each socket address is normally permitted
            tcpClientHost = _socketFactory.CreateTcpClient(request.DestinationEndPoint);
            VhUtils.ConfigTcpClient(tcpClientHost, _tcpKernelSendBufferSize, _tcpKernelReceiveBufferSize);

            // connect to requested destination
            isRequestedEpException = true;
            await tcpClientHost.ConnectAsync(request.DestinationEndPoint, cancellationToken).Vhc();
            isRequestedEpException = false;

            //tracking
            LogTrack(IpProtocol.Tcp,
                localEndPoint: (IPEndPoint?)tcpClientHost.Client.LocalEndPoint,
                destinationEndPoint: request.DestinationEndPoint,
                isNewLocal: true, isNewRemote: true, failReason: null);

            // send response, using original cancellation token without timeout
            // ReSharper disable once PossiblyMistakenUseOfCancellationToken
            await clientStream.WriteResponseAsync(SessionResponseEx, cancellationToken).Vhc();

            // add the connection
            VhLogger.Instance.LogDebug(GeneralEventId.ProxyChannel,
                "Adding a ProxyChannel. SessionId: {SessionId}", VhLogger.FormatSessionId(SessionId));

            tcpClientStreamHost = new TcpClientStream(tcpClientHost, tcpClientHost.GetStream(), 
                request.RequestId + ":host");
            proxyChannel = new ProxyChannel(request.RequestId, tcpClientStreamHost, clientStream,
                _tcpBufferSize, _tcpBufferSize);

            Tunnel.AddChannel(proxyChannel);
        }
        catch (Exception ex) {
            tcpClientHost?.Dispose();
            tcpClientStreamHost?.Dispose();
            proxyChannel?.Dispose();

            if (isRequestedEpException)
                throw new ServerSessionException(clientStream.IpEndPointPair.RemoteEndPoint,
                    this, SessionErrorCode.GeneralError, request.RequestId, ex.Message);
            throw;
        }
        finally {
            if (isTcpConnectIncreased)
                Interlocked.Decrement(ref _tcpConnectWaitCount);
        }
    }

    private void VerifyTcpChannelRequest(IClientStream clientStream, StreamProxyChannelRequest request)
    {
        // filter
        var newEndPoint = _netFilter.ProcessRequest(IpProtocol.Tcp, request.DestinationEndPoint);
        if (newEndPoint == null) {
            LogTrack(IpProtocol.Tcp, null, request.DestinationEndPoint, false, true, "NetFilter");
            _filterReporter.Raise();
            throw new RequestBlockedException(request.DestinationEndPoint, this, request.RequestId);
        }

        request.DestinationEndPoint = newEndPoint;

        lock (_verifyRequestLock) {
            // NetScan limit
            VerifyNetScan(IpProtocol.Tcp, request.DestinationEndPoint, request.RequestId);

            // Channel Count limit
            if (TcpChannelCount >= _maxTcpChannelCount) {
                LogTrack(IpProtocol.Tcp, null, request.DestinationEndPoint, false, true, "MaxTcp");
                _maxTcpChannelExceptionReporter.Raise();
                throw new MaxTcpChannelException(clientStream.IpEndPointPair.RemoteEndPoint, this, request.RequestId);
            }

            // Check tcp wait limit
            if (TcpConnectWaitCount >= _maxTcpConnectWaitCount) {
                LogTrack(IpProtocol.Tcp, null, request.DestinationEndPoint, false, true, "MaxTcpWait");
                _maxTcpConnectWaitExceptionReporter.Raise();
                throw new MaxTcpConnectWaitException(clientStream.IpEndPointPair.RemoteEndPoint, this,
                    request.RequestId);
            }
        }
    }

    private void VerifyNetScan(IpProtocol protocol, IPEndPoint remoteEndPoint, string requestId)
    {
        if (NetScanDetector == null || NetScanDetector.Verify(remoteEndPoint)) return;

        LogTrack(protocol, null, remoteEndPoint, false, true, "NetScan");
        _netScanExceptionReporter.Raise();
        throw new NetScanException(remoteEndPoint, this, requestId);
    }

    public void Dispose()
    {
        if (IsDisposed) return;

        _proxyManager.PacketReceived -= Proxy_PacketsReceived;
        _proxyManager.Dispose();
        Tunnel.PacketReceived -= Tunnel_PacketReceived;
        Tunnel.Dispose();
        _netScanExceptionReporter.Dispose();
        _maxTcpChannelExceptionReporter.Dispose();
        _maxTcpConnectWaitExceptionReporter.Dispose();
        NetScanDetector?.Dispose();

        // if there is no reason it is temporary
        var reason = "Cleanup";
        if (SessionResponseEx.ErrorCode != SessionErrorCode.Ok)
            reason = SessionResponseEx.ErrorCode == SessionErrorCode.SessionClosed ? "User" : "Access";

        // Report removing session
        VhLogger.Instance.LogInformation(GeneralEventId.SessionTrack,
            "SessionId: {SessionId-5}\t{Mode,-5}\tActor: {Actor,-7}\tSuppressBy: {SuppressedBy,-8}\tErrorCode: {ErrorCode,-20}\tMessage: {message}",
            SessionId, "Close", reason, SessionResponseEx.SuppressedBy, SessionResponseEx.ErrorCode,
            SessionResponseEx.ErrorMessage ?? "None");

        // it must be ended to let manager know that session is disposed and finish all tasks
        DisposedTime = DateTime.UtcNow;
        SetSyncRequired();
    }

    private class PacketProxyCallbacks(Session session) : IPacketProxyCallbacks
    {
        public void OnConnectionRequested(IpProtocol protocolType, IPEndPoint remoteEndPoint)
        {
            session.VerifyNetScan(protocolType, remoteEndPoint, "OnNewRemoteEndPoint");
        }

        public void OnConnectionEstablished(IpProtocol protocolType, IPEndPoint localEndPoint,
            IPEndPoint remoteEndPoint,
            bool isNewLocalEndPoint, bool isNewRemoteEndPoint)
        {
            session.LogTrack(protocolType, localEndPoint, remoteEndPoint, isNewLocalEndPoint,
                isNewRemoteEndPoint, null);
        }
    }
}