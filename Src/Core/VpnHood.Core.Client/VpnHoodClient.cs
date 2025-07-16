﻿using Ga4.Trackers;
using Ga4.Trackers.Ga4Tags;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using VpnHood.Core.Client.Abstractions;
using VpnHood.Core.Client.ConnectorServices;
using VpnHood.Core.Client.DomainFiltering;
using VpnHood.Core.Client.Exceptions;
using VpnHood.Core.Common.Exceptions;
using VpnHood.Core.Common.Messaging;
using VpnHood.Core.Common.Tokens;
using VpnHood.Core.Common.Trackers;
using VpnHood.Core.Packets;
using VpnHood.Core.Packets.Extensions;
using VpnHood.Core.Toolkit.Jobs;
using VpnHood.Core.Toolkit.Logging;
using VpnHood.Core.Toolkit.Net;
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

namespace VpnHood.Core.Client;

public class VpnHoodClient : IDisposable, IAsyncDisposable
{
    private const int MaxProtocolVersion = 9;
    private const int MinProtocolVersion = 4;
    private bool _disposedInternal;
    private bool _disposed;
    private readonly bool _autoDisposeVpnAdapter;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly ProxyManager _proxyManager;
    private readonly Dictionary<IPAddress, bool> _includeIps = new();
    private readonly int _maxPacketChannelCount;
    private readonly IVpnAdapter _vpnAdapter;
    private readonly ClientHost _clientHost;
    private readonly TimeSpan _minPacketChannelLifespan;
    private readonly TimeSpan _maxPacketChannelLifespan;
    private readonly bool _allowAnonymousTracker;
    private ClientUsageTracker? _clientUsageTracker;
    private DateTime? _initConnectedTime;
    private DateTime? _lastConnectionErrorTime;
    private byte[]? _sessionKey;
    private bool _useUdpChannel;
    private ClientState _state = ClientState.None;
    private ConnectorService? _connectorService;
    private readonly TimeSpan _tcpConnectTimeout;
    private DateTime? _autoWaitTime;
    private readonly ServerFinder _serverFinder;
    private readonly ConnectPlanId _planId;
    private readonly string? _accessCode;
    private readonly TimeSpan _canExtendByRewardedAdThreshold;
    private bool _isTunProviderSupported;
    private bool _isDnsServersAccepted;
    private readonly bool _allowRewardedAd;
    private ulong? _sessionId;
    private readonly string[]? _includeApps;
    private readonly string[]? _excludeApps;
    private ClientSessionStatus? _sessionStatus;
    private IPAddress[] _dnsServers;
    private readonly int _udpSendBufferSize;
    private readonly int _udpReceiveBufferSize;
    private int _sessionPacketChannelCount;
    private readonly AsyncLock _packetChannelLock = new();
    private readonly Job _cleanupJob;
    private readonly Tunnel _tunnel;

    private ConnectorService ConnectorService => VhUtils.GetRequiredInstance(_connectorService);
    public ISocketFactory SocketFactory { get; }
    public event EventHandler? StateChanged;
    public bool IsIpV6SupportedByServer { get; private set; }
    public bool IsIpV6SupportedByClient { get; internal set; }
    public TimeSpan SessionTimeout { get; set; }
    public TimeSpan AutoWaitTimeout { get; set; }
    public TimeSpan ReconnectTimeout { get; set; }
    public Token Token { get; }
    public string ClientId { get; }
    public Version Version { get; }
    public bool IncludeLocalNetwork { get; }
    public IpRangeOrderedList IncludeIpRanges { get; private set; }
    public IpRangeOrderedList VpnAdapterIncludeIpRanges { get; private set; }
    public string UserAgent { get; }
    public IPEndPoint? HostTcpEndPoint => _connectorService?.EndPointInfo.TcpEndPoint;
    public IPEndPoint? HostUdpEndPoint { get; private set; }
    public bool DropUdp { get; set; }
    public bool DropQuic { get; set; }
    public bool UseTcpOverTun { get; set; }
    public byte[] SessionKey => _sessionKey ?? throw new InvalidOperationException($"{nameof(SessionKey)} has not been initialized.");
    public byte[]? ServerSecret { get; private set; }
    public DomainFilterService DomainFilterService { get; }
    public bool AllowTcpReuse { get; }
    public ClientAdService AdService { get; init; }
    public string? SessionName { get; }
    public ulong SessionId => _sessionId ?? throw new InvalidOperationException("SessionId has not been initialized.");
    public ISessionStatus? SessionStatus => _sessionStatus;
    public SessionInfo? SessionInfo { get; private set; }
    public Exception? LastException { get; private set; }
    public ITracker? Tracker { get; }

    public VpnHoodClient(
        IVpnAdapter vpnAdapter,
        ISocketFactory socketFactory,
        ITracker? tracker,
        ClientOptions options)
    {
        if (options.TcpProxyCatcherAddressIpV4 == null)
            throw new ArgumentNullException(nameof(options.TcpProxyCatcherAddressIpV4));

        if (options.TcpProxyCatcherAddressIpV6 == null)
            throw new ArgumentNullException(nameof(options.TcpProxyCatcherAddressIpV6));

        if (!VhUtils.IsInfinite(_maxPacketChannelLifespan) && _maxPacketChannelLifespan < _minPacketChannelLifespan)
            throw new ArgumentNullException(nameof(options.MaxPacketChannelTimespan),
                $"{nameof(options.MaxPacketChannelTimespan)} must be bigger or equal than {nameof(options.MinPacketChannelTimespan)}.");

        var token = Token.FromAccessKey(options.AccessKey);
        socketFactory = new AdapterSocketFactory(vpnAdapter, socketFactory);
        SocketFactory = socketFactory;
        _dnsServers = options.DnsServers ?? [];
        _allowAnonymousTracker = options.AllowAnonymousTracker;
        _minPacketChannelLifespan = options.MinPacketChannelTimespan;
        _maxPacketChannelLifespan = options.MaxPacketChannelTimespan;
        _vpnAdapter = vpnAdapter;
        _autoDisposeVpnAdapter = options.AutoDisposeVpnAdapter;
        _maxPacketChannelCount = options.MaxPacketChannelCount;
        Tracker = tracker;
        _tcpConnectTimeout = options.ConnectTimeout;
        _useUdpChannel = options.UseUdpChannel;
        _planId = options.PlanId;
        _accessCode = options.AccessCode;
        _excludeApps = options.ExcludeApps;
        _includeApps = options.IncludeApps;
        _allowRewardedAd = options.AllowRewardedAd;
        _udpReceiveBufferSize = options.UdpReceiveBufferSize ?? TunnelDefaults.ClientUdpReceiveBufferSize;
        _udpSendBufferSize = options.UdpSendBufferSize ?? TunnelDefaults.ClientUdpSendBufferSize;
        _canExtendByRewardedAdThreshold = options.CanExtendByRewardedAdThreshold;
        _serverFinder = new ServerFinder(socketFactory, token.ServerToken,
            serverLocation: options.ServerLocation,
            serverQueryTimeout: options.ServerQueryTimeout,
            tracker: options.AllowEndPointTracker ? tracker : null);
        _proxyManager = new ProxyManager(socketFactory, new ProxyManagerOptions {
            IsPingSupported = false,
            PacketProxyCallbacks = null,
            UdpTimeout = TunnelDefaults.UdpTimeout,
            MaxUdpClientCount = TunnelDefaults.MaxUdpClientCount,
            MaxPingClientCount = TunnelDefaults.MaxPingClientCount,
            PacketQueueCapacity = TunnelDefaults.ProxyPacketQueueCapacity,
            IcmpTimeout = TunnelDefaults.IcmpTimeout,
            UdpReceiveBufferSize = TunnelDefaults.ClientUdpReceiveBufferSize,
            UdpSendBufferSize = TunnelDefaults.ClientUdpSendBufferSize,
            LogScope = null,
            UseUdpProxy2 = true,
            AutoDisposePackets = true,
        });
        _proxyManager.PacketReceived += Proxy_PacketReceived;


        SessionName = options.SessionName;
        AllowTcpReuse = options.AllowTcpReuse;
        ReconnectTimeout = options.ReconnectTimeout;
        AutoWaitTimeout = options.AutoWaitTimeout;
        Token = token;
        Version = options.Version;
        UserAgent = options.UserAgent;
        ClientId = options.ClientId;
        SessionTimeout = options.SessionTimeout;
        IncludeLocalNetwork = options.IncludeLocalNetwork;
        DropUdp = options.DropUdp;
        DropQuic = options.DropQuic;
        UseTcpOverTun = options.UseTcpOverTun;
        var dnsRange = options.DnsServers?.Select(x => new IpRange(x)).ToArray() ?? [];
        VpnAdapterIncludeIpRanges = options.VpnAdapterIncludeIpRanges.ToOrderedList().Union(dnsRange);
        IncludeIpRanges = options.IncludeIpRanges.ToOrderedList().Union(dnsRange);
        AdService = new ClientAdService(this);

        // SNI is sensitive, must be explicitly enabled
        DomainFilterService = new DomainFilterService(options.DomainFilter, forceLogSni: options.ForceLogSni);

        // Tunnel
        _tunnel = new Tunnel(new TunnelOptions {
            AutoDisposePackets = true,
            PacketQueueCapacity = TunnelDefaults.TunnelPacketQueueCapacity,
            MaxPacketChannelCount = TunnelDefaults.MaxPacketChannelCount,
            UseSpeedometerTimer = true
        });
        _tunnel.PacketReceived += Tunnel_PacketReceived;

        // create proxy host
        _clientHost = new ClientHost(this, _tunnel, options.TcpProxyCatcherAddressIpV4, options.TcpProxyCatcherAddressIpV6);
        _clientHost.PacketReceived += ClientHost_PacketReceived;

        // init vpnAdapter events
        vpnAdapter.Disposed += (_, _) => _ = DisposeAsync();
        vpnAdapter.PacketReceived += VpnAdapter_PacketReceived;

        // Create simple disposable objects
        _cancellationTokenSource = new CancellationTokenSource();
        _cleanupJob = new Job(Cleanup, nameof(VpnHoodClient));
    }

    public ClientState State {
        get => _state;
        private set {
            if (_state == value) return;
            _state = value; //must set before raising the event; 
            VhLogger.Instance.LogInformation("Client state is changed. NewState: {NewState}", State);
            Task.Run(() => {
                StateChanged?.Invoke(this, EventArgs.Empty);
                if (value == ClientState.Disposed)
                    StateChanged = null; //no more event will be raised after disposed
            }, CancellationToken.None);
        }
    }

    private bool CanExtendByRewardedAd(AccessUsage? accessUsage)
    {
        return
            accessUsage is { CanExtendByRewardedAd: true, ExpirationTime: not null } &&
            accessUsage.ExpirationTime > FastDateTime.UtcNow + _canExtendByRewardedAdThreshold &&
            _allowRewardedAd &&
            Token.IsPublic;
    }


    public bool UseUdpChannel {
        get => _useUdpChannel;
        set {
            if (_useUdpChannel == value) return;
            _useUdpChannel = value;
            _tunnel.MaxPacketChannelCount = value ? 1 : _maxPacketChannelCount;
            _tunnel.RemoveAllPacketChannels();
            Task.Run(() => ManagePacketChannels(_cancellationTokenSource.Token));
        }
    }

    internal async Task AddPassthruTcpStream(IClientStream orgTcpClientStream, IPEndPoint hostEndPoint,
        string channelId, byte[] initBuffer, CancellationToken cancellationToken)
    {
        // set timeout
        using var timeoutCts = new CancellationTokenSource(ConnectorService.RequestTimeout);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        // connect to host
        var tcpClient = SocketFactory.CreateTcpClient(hostEndPoint);
        await tcpClient.ConnectAsync(hostEndPoint.Address, hostEndPoint.Port, connectCts.Token).Vhc();

        // create and add the channel
        var channel = new ProxyChannel(channelId, orgTcpClientStream,
            new TcpClientStream(tcpClient, tcpClient.GetStream(), channelId + ":host"));

        // flush initBuffer
        await tcpClient.GetStream().WriteAsync(initBuffer, connectCts.Token);

        try {
            _proxyManager.AddChannel(channel);
        }
        catch {
            channel.Dispose();
            throw;
        }
    }

    public async Task Connect(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(VhLogger.FormatType(this));

        // Connect
        try {
            // merge cancellation tokens
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken);

            // create connection log scope
            using var scope = VhLogger.Instance.BeginScope("Client");
            if (State != ClientState.None)
                throw new Exception("Connection is already in progress.");

            // Preparing device;
            if (_vpnAdapter.IsStarted) //make sure it is not a shared packet capture
                throw new InvalidOperationException("VpnAdapter should not be started before connect.");

            // Connecting. Must before IsIpv6Supported
            State = ClientState.Connecting;

            // report config
            IsIpV6SupportedByClient = await IPAddressUtil.IsIpv6Supported();
            _proxyManager.IsIpV6Supported = IsIpV6SupportedByClient;
            _serverFinder.IncludeIpV6 = IsIpV6SupportedByClient;
            ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
            VhLogger.Instance.LogInformation(
                "UseUdpChannel: {UseUdpChannel}, DropUdp: {DropUdp}, DropQuic: {DropQuic}, UseTcpOverTun: {UseTcpOverTun}, " +
                "IncludeLocalNetwork: {IncludeLocalNetwork}, MinWorkerThreads: {WorkerThreads}, " +
                "CompletionPortThreads: {CompletionPortThreads}, ClientIpV6: {ClientIpV6}, ProcessId: {ProcessId}",
                UseUdpChannel, DropUdp, DropQuic, UseTcpOverTun, IncludeLocalNetwork, workerThreads,
                completionPortThreads, IsIpV6SupportedByClient, Process.GetCurrentProcess().Id);

            // report version
            VhLogger.Instance.LogInformation(
                "ClientVersion: {ClientVersion}, " +
                "ClientMinProtocolVersion: {ClientMinProtocolVersion}, ClientMaxProtocolVersion: {ClientMaxProtocolVersion}, " +
                "ClientId: {ClientId}",
                Version, MinProtocolVersion, MaxProtocolVersion, VhLogger.FormatId(ClientId));


            // Establish first connection and create a session
            var hostEndPoint = await _serverFinder.FindReachableServerAsync(linkedCts.Token).Vhc();
            await ConnectInternal(hostEndPoint, true, linkedCts.Token).Vhc();

            // Create Tcp Proxy Host
            _clientHost.Start();

            State = ClientState.Connected;
            _initConnectedTime = DateTime.UtcNow;
        }
        catch (Exception ex) {
            // clear before start new async task
            await DisposeAsync(ex);
            throw;
        }
    }

    private IpRangeOrderedList BuildVpnAdapterIncludeIpRanges(IPAddress hostIpAddress)
    {
        // Start with user VpnAdapterIncludeIpRanges
        var includeIpRanges = VpnAdapterIncludeIpRanges;

        // exclude server if ProtectClient is not supported to prevent loop
        if (!_vpnAdapter.CanProtectSocket)
            includeIpRanges = includeIpRanges.Exclude(hostIpAddress);

        // exclude local networks
        includeIpRanges = includeIpRanges
            .Exclude(IpNetwork.LocalNetworks.ToIpRanges());

        // Make sure CatcherAddress is included
        includeIpRanges = includeIpRanges.Union([
            new IpRange(_clientHost.CatcherAddressIpV4),
            new IpRange(_clientHost.CatcherAddressIpV6)
        ]);

        return includeIpRanges; //sort and unify
    }

    // WARNING: Performance Critical!
    private void ClientHost_PacketReceived(object? sender, IpPacket ipPacket)
    {
        _vpnAdapter.SendPacketQueued(ipPacket);
    }

    // WARNING: Performance Critical!
    private void Proxy_PacketReceived(object? sender, IpPacket ipPacket)
    {
        _vpnAdapter.SendPacketQueued(ipPacket);
    }

    // WARNING: Performance Critical!
    private void Tunnel_PacketReceived(object? sender, IpPacket ipPacket)
    {
        _vpnAdapter.SendPacketQueued(ipPacket);
    }

    // WARNING: Performance Critical! Mango Section
    private void VpnAdapter_PacketReceived(object? sender, IpPacket ipPacket)
    {
        // stop traffic if the client has been disposed
        if (_disposed || _initConnectedTime is null)
            return;

        // stop traffic if the client is paused and unpause after AutoPauseTimeout
        if (_autoWaitTime != null) {
            if (FastDateTime.Now - _autoWaitTime.Value < AutoWaitTimeout)
                throw new PacketDropException("Connection is paused. The packet has been dropped.");

            // resume connection if the client is paused and AutoWaitTimeout is not set
            _autoWaitTime = null;
            State = ClientState.Connecting;
        }

        // Manage datagram channels if needed
        if (ShouldManagePacketChannels && !_packetChannelLock.IsLocked)
            _ = ManagePacketChannels(_cancellationTokenSource.Token);

        // Multicast packets are not supported (Already excluded by adapter filter)
        if (ipPacket.IsMulticast()) {
            PacketLogger.LogPacket(ipPacket, "A multicast packet has been dropped.");
            ipPacket.Dispose();
            return;
        }

        // TcpHost has to manage its own packets
        if (_clientHost.IsOwnPacket(ipPacket)) {
            _clientHost.ProcessOutgoingPacket(ipPacket);
            return;
        }

        // tcp already check for InInRange and IpV6 and Proxy
        if (ipPacket.Protocol == IpProtocol.Tcp) {
            if (_isTunProviderSupported && UseTcpOverTun && IsInIpRange(ipPacket.DestinationAddress))
                _tunnel.SendPacketQueuedAsync(ipPacket).VhBlock();
            else
                _clientHost.ProcessOutgoingPacket(ipPacket);
            return;
        }

        // use local proxy if the packet is not in the range and not ICMP.
        // ICMP is not supported by the local proxy for split tunnel
        if (!IsInIpRange(ipPacket.DestinationAddress) && !ipPacket.IsIcmpEcho()) {
            _proxyManager.SendPacketQueued(ipPacket);
            return;
        }

        // Drop IPv6 if not support
        if (ipPacket.IsV6() && !IsIpV6SupportedByServer)
            throw new PacketDropException("IPv6 packet has been dropped because server does not support IPv6.");

        // ICMP packet must go through tunnel because PingProxy does not support protect socket
        if (ipPacket.IsIcmpEcho()) {
            // ICMP can not be proxied so we don't need to check InRange
            _tunnel.SendPacketQueuedAsync(ipPacket).VhBlock();
            return;
        }

        // Udp
        if (ipPacket.Protocol == IpProtocol.Udp && ShouldTunnelUdpPacket(ipPacket.ExtractUdp())) {
            _tunnel.SendPacketQueuedAsync(ipPacket).VhBlock();
            return;
        }

        // Drop packet
        throw new PacketDropException("Packet has been dropped because no one handle it.");
    }

    private bool ShouldTunnelUdpPacket(UdpPacket udpPacket)
    {
        if (DropUdp)
            return false;

        if (DropQuic && udpPacket.DestinationPort is 80 or 443)
            return false;

        return true;
    }

    public bool IsInIpRange(IPAddress ipAddress)
    {
        // all IPs are included if there is no filter
        if (IncludeIpRanges.Count == 0)
            return true;

        // check tcp-loopback
        if (ipAddress.Equals(_clientHost.CatcherAddressIpV4) ||
            ipAddress.Equals(_clientHost.CatcherAddressIpV6))
            return true;

        // check the cache
        if (_includeIps.TryGetValue(ipAddress, out var isInRange))
            return isInRange;

        // check include
        isInRange = IncludeIpRanges.IsInRange(ipAddress);

        // cache the result
        // we don't need to keep that much ips in the cache
        if (_includeIps.Count > 0xFFFF) {
            VhLogger.Instance.LogInformation("Clearing IP filter cache!");
            _includeIps.Clear();
        }

        _includeIps.Add(ipAddress, isInRange);
        return isInRange;
    }

    private bool ShouldManagePacketChannels =>
        _tunnel.PacketChannelCount < _tunnel.MaxPacketChannelCount;

    private async ValueTask ManagePacketChannels(CancellationToken cancellationToken)
    {
        // if the lock is not acquired, it means that another thread is already managing packet channels
        using var lockResult = await _packetChannelLock.LockAsync(TimeSpan.Zero, cancellationToken);
        if (!lockResult.Succeeded)
            return;

        try {
            // check is adding channels allowed
            if (!ShouldManagePacketChannels)
                return;

            // make sure only one UdpChannel exists for PacketChannels if UseUdpChannel is on
            if (UseUdpChannel)
                AddUdpChannel();
            else
                await AddTcpPacketChannel(cancellationToken).Vhc();
        }
        catch (Exception ex) {
            if (!_disposed)
                VhLogger.LogError(GeneralEventId.PacketChannel, ex, "Could not Manage PacketChannels.");
        }
    }

    private void AddUdpChannel()
    {
        if (HostTcpEndPoint == null) throw new InvalidOperationException($"{nameof(HostTcpEndPoint)} is not initialized!");
        if (VhUtils.IsNullOrEmpty(ServerSecret)) throw new Exception("ServerSecret has not been set.");
        if (VhUtils.IsNullOrEmpty(_sessionKey)) throw new Exception("Server UdpKey has not been set.");
        if (HostUdpEndPoint == null) {
            UseUdpChannel = false;
            throw new Exception("Server does not serve any UDP endpoint.");
        }

        var udpChannel = ClientUdpChannelFactory.Create(
            new ClientUdpChannelOptions {
                SocketFactory = SocketFactory,
                ServerKey = ServerSecret,
                RemoteEndPoint = HostUdpEndPoint,
                SessionKey = _sessionKey,
                SessionId = SessionId,
                ProtocolVersion = ConnectorService.ProtocolVersion,
                AutoDisposePackets = true,
                Blocking = true,
                ChannelId = Guid.NewGuid().ToString(),
                Lifespan = null,
                UdpReceiveBufferSize = _udpReceiveBufferSize,
                UdpSendBufferSize = _udpSendBufferSize
            });

        try {
            _tunnel.AddChannel(udpChannel);
        }
        catch {
            udpChannel.Dispose();
            UseUdpChannel = false;
            throw;
        }
    }

    private async Task ConnectInternal(IPEndPoint hostEndPoint, bool allowRedirect, CancellationToken cancellationToken)
    {
        try {
            VhLogger.Instance.LogInformation("Connecting to the server... EndPoint: {hostEndPoint}", VhLogger.Format(hostEndPoint));
            State = ClientState.Connecting;

            // create connector service
            _connectorService = new ConnectorService(
                new ConnectorEndPointInfo {
                    HostName = Token.ServerToken.HostName,
                    TcpEndPoint = hostEndPoint,
                    CertificateHash = Token.ServerToken.CertificateHash
                },
                socketFactory: SocketFactory,
                requestTimeout: _tcpConnectTimeout,
                allowTcpReuse: false);

            // send hello request
            var clientInfo = new ClientInfo {
                ClientId = ClientId,
                ClientVersion = Version.ToString(3),
                MinProtocolVersion = _connectorService.ProtocolVersion,
                MaxProtocolVersion = MaxProtocolVersion,
                UserAgent = UserAgent
            };

            var request = new HelloRequest {
                RequestId = UniqueIdFactory.Create(),
                EncryptedClientId = VhUtils.EncryptClientId(clientInfo.ClientId, Token.Secret),
                ClientInfo = clientInfo,
                TokenId = Token.TokenId,
                ServerLocation = _serverFinder.ServerLocation,
                PlanId = _planId,
                AccessCode = _accessCode,
                AllowRedirect = allowRedirect,
                IsIpV6Supported = IsIpV6SupportedByClient
            };

            using var requestResult = await SendRequest<HelloResponse>(request, cancellationToken).Vhc();
            requestResult.ClientStream.PreventReuse(); // lets hello request stream not to be reused
            _connectorService.AllowTcpReuse = AllowTcpReuse; // after hello, we can reuse, as the other connections can use websocket

            var helloResponse = requestResult.Response;
            if (helloResponse.ClientPublicAddress is null)
                throw new NotSupportedException($"Server must returns {nameof(helloResponse.ClientPublicAddress)}.");


            // initialize the connector
            _connectorService.Init(
                helloResponse.ProtocolVersion,
                requestTimeout: Debugger.IsAttached ? VhUtils.DebuggerTimeout : helloResponse.RequestTimeout,
                tcpReuseTimeout: helloResponse.TcpReuseTimeout,
                serverSecret: helloResponse.ServerSecret,
                useWebSocket: true);

            // log response
            VhLogger.Instance.LogInformation(GeneralEventId.Session,
                "Hurray! Client has been connected! " +
                $"SessionId: {VhLogger.FormatId(helloResponse.SessionId)}, " +
                $"ServerVersion: {helloResponse.ServerVersion}, " +
                $"ProtocolVersion: {helloResponse.ProtocolVersion}, " +
                $"CurrentProtocolVersion: {_connectorService.ProtocolVersion}, " +
                $"ClientIp: {VhLogger.Format(helloResponse.ClientPublicAddress)}, " +
                $"IsTunProviderSupported: {helloResponse.IsTunProviderSupported}, " +
                $"NetworkV4: {helloResponse.VirtualIpNetworkV4}, " +
                $"NetworkV6: {helloResponse.VirtualIpNetworkV6}, " +
                $"ClientCountry: {helloResponse.ClientCountry}");

            // get session id
            _sessionId = helloResponse.SessionId;
            _sessionKey = helloResponse.SessionKey;
            _isTunProviderSupported = helloResponse.IsTunProviderSupported;
            ServerSecret = helloResponse.ServerSecret;
            IsIpV6SupportedByServer = helloResponse.IsIpV6Supported;

            if (helloResponse.UdpPort > 0)
                HostUdpEndPoint = new IPEndPoint(_connectorService.EndPointInfo.TcpEndPoint.Address,
                    helloResponse.UdpPort.Value);

            // VpnAdapterIpRanges
            if (!VhUtils.IsNullOrEmpty(helloResponse.VpnAdapterIncludeIpRanges))
                VpnAdapterIncludeIpRanges =
                    VpnAdapterIncludeIpRanges.Intersect(helloResponse.VpnAdapterIncludeIpRanges);

            // IncludeIpRanges
            if (!VhUtils.IsNullOrEmpty(helloResponse.IncludeIpRanges) &&
                !helloResponse.IncludeIpRanges.ToOrderedList().IsAll())
                IncludeIpRanges = IncludeIpRanges.Intersect(helloResponse.IncludeIpRanges);

            // set DNS after setting IpFilters
            VhLogger.Instance.LogInformation("Configuring Client DNS servers... DnsServers: {DnsServers}",
                string.Join(", ", _dnsServers.Select(x => x.ToString())));
            _isDnsServersAccepted = VhUtils.IsNullOrEmpty(_dnsServers) || _dnsServers.Any(IsInIpRange); // no servers means accept default
            if (!_isDnsServersAccepted)
                VhLogger.Instance.LogWarning(
                    "Client DNS servers have been ignored because the server does not route them.");

            _dnsServers = _dnsServers.Where(IsInIpRange).ToArray();
            if (VhUtils.IsNullOrEmpty(_dnsServers)) {
                _dnsServers = VhUtils.IsNullOrEmpty(helloResponse.DnsServers)
                    ? IPAddressUtil.GoogleDnsServers
                    : helloResponse.DnsServers;
                IncludeIpRanges = IncludeIpRanges.Union(_dnsServers.Select(IpRange.FromIpAddress));
            }

            if (VhUtils.IsNullOrEmpty(_dnsServers?.Where(IsInIpRange)
                    .ToArray())) // make sure there is at least one DNS server
                throw new Exception("Could not specify any DNS server. The server is not configured properly.");

            VhLogger.Instance.LogInformation("DnsServers: {DnsServers}",
                string.Join(", ", _dnsServers.Select(VhLogger.Format)));

            // report Suppressed
            if (helloResponse.SuppressedTo == SessionSuppressType.YourSelf)
                VhLogger.Instance.LogWarning("You suppressed a session of yourself!");

            else if (helloResponse.SuppressedTo == SessionSuppressType.Other)
                VhLogger.Instance.LogWarning("You suppressed a session of another client!");

            // disable UdpChannel if not supported
            if (HostUdpEndPoint is null) {
                VhLogger.Instance.LogWarning("Server does not support UDP channel.");
                _useUdpChannel = false;
            }

            // set the session info
            SessionInfo = new SessionInfo {
                SessionId = helloResponse.SessionId.ToString(),
                ClientPublicIpAddress = helloResponse.ClientPublicAddress,
                ClientCountry = helloResponse.ClientCountry,
                AccessInfo = helloResponse.AccessInfo ?? new AccessInfo(),
                IsDnsServersAccepted = _isDnsServersAccepted,
                DnsServers = _dnsServers,
                IsPremiumSession = helloResponse.AccessUsage?.IsPremium ?? false,
                IsUdpChannelSupported = HostUdpEndPoint != null,
                AccessKey = helloResponse.AccessKey,
                ServerVersion = Version.Parse(helloResponse.ServerVersion),
                SuppressedTo = helloResponse.SuppressedTo,
                ServerLocationInfo = helloResponse.ServerLocation != null
                    ? ServerLocationInfo.Parse(helloResponse.ServerLocation)
                    : null
            };

            // set session status
            _sessionStatus = new ClientSessionStatus(this, helloResponse.AccessUsage ?? new AccessUsage());

            // show ad
            var adResult = await ShowAd(helloResponse.AdRequirement, helloResponse.SessionId, cancellationToken);

            // usage trackers
            if (_allowAnonymousTracker) {
                // Anonymous server usage tracker
                if (!string.IsNullOrEmpty(helloResponse.GaMeasurementId)) {
                    var ga4Tracking = new Ga4TagTracker {
                        SessionCount = 1,
                        MeasurementId = helloResponse.GaMeasurementId,
                        ClientId = ClientId,
                        SessionId = helloResponse.SessionId.ToString(),
                        UserAgent = UserAgent,
                        UserProperties = new Dictionary<string, object> { { "client_version", Version.ToString(3) } }
                    };

                    _ = ga4Tracking.TryTrack(new Ga4TagEvent { EventName = TrackEventNames.SessionStart }, VhLogger.Instance);
                }

                // Anonymous app usage tracker
                if (Tracker != null) {
                    _ = Tracker.TryTrack(ClientTrackerBuilder.BuildConnectionSucceeded(
                        _serverFinder.ServerLocation,
                        isIpV6Supported: IsIpV6SupportedByClient,
                        hasRedirected: !allowRedirect,
                        endPoint: _connectorService.EndPointInfo.TcpEndPoint,
                        adNetworkName: adResult?.NetworkName));

                    _clientUsageTracker = new ClientUsageTracker(_sessionStatus, Tracker);
                }
            }

            // Preparing tunnel
            VhLogger.Instance.LogInformation("Configuring Packet Channels...");
            _tunnel.RemoteMtu = helloResponse.Mtu;
            _tunnel.MaxPacketChannelCount = helloResponse.MaxPacketChannelCount != 0
                ? _tunnel.MaxPacketChannelCount =
                    Math.Min(_maxPacketChannelCount, helloResponse.MaxPacketChannelCount)
                : _maxPacketChannelCount;

            // manage datagram channels
            await ManagePacketChannels(cancellationToken).Vhc();

            // prepare packet capture
            // Set a default to capture & drop the packets if the server does not provide a network
            var networkV4 = helloResponse.VirtualIpNetworkV4 ?? new IpNetwork(IPAddress.Parse("10.255.0.2"), 32);
            var networkV6 = helloResponse.VirtualIpNetworkV6 ?? new IpNetwork(IPAddressUtil.GenerateUlaAddress(0x1001), 128);
            var longIncludeNetworks = string.Join(", ", VpnAdapterIncludeIpRanges.ToIpNetworks().Select(VhLogger.Format));
            VhLogger.Instance.LogInformation(
                "Starting VpnAdapter... DnsServers: {DnsServers}, IncludeNetworks: {longIncludeNetworks}",
                SessionInfo.DnsServers, longIncludeNetworks);

            // Build the IncludeIpRanges for the VpnAdapter
            var adapterIncludeRanges = BuildVpnAdapterIncludeIpRanges(_connectorService.EndPointInfo.TcpEndPoint.Address);

            // sometimes packet goes directly to the adapter especially on windows, so we need to filter them
            IncludeIpRanges = IncludeIpRanges.Intersect(adapterIncludeRanges);

            // Start the VpnAdapter
            var adapterOptions = new VpnAdapterOptions {
                DnsServers = SessionInfo.DnsServers,
                VirtualIpNetworkV4 = networkV4,
                VirtualIpNetworkV6 = networkV6,
                Mtu = helloResponse.Mtu - TunnelDefaults.MtuOverhead,
                IncludeNetworks = adapterIncludeRanges.ToIpNetworks().ToArray(),
                SessionName = SessionName,
                ExcludeApps = _excludeApps,
                IncludeApps = _includeApps
            };
            await _vpnAdapter.Start(adapterOptions, cancellationToken);
        }
        catch (RedirectHostException ex) {
            if (!allowRedirect) {
                VhLogger.Instance.LogError(ex,
                    "The server replies with a redirect to another server again. We already redirected earlier. This is unexpected.");
                throw;
            }

            // init new connector
            _connectorService?.Dispose();
            var redirectedEndPoint = await _serverFinder.FindBestRedirectedServerAsync(ex.RedirectHostEndPoints.ToArray(), cancellationToken);
            await ConnectInternal(redirectedEndPoint, false, cancellationToken).Vhc();
        }
    }

    private async Task<AdResult?> ShowAd(AdRequirement adRequirement, ulong sessionId, CancellationToken cancellationToken)
    {
        if (adRequirement == AdRequirement.None)
            return null;

        var prevState = State;
        using var autoDispose = new AutoDispose(() => State = prevState);
        State = ClientState.WaitingForAd;

        var adResult = adRequirement switch {
            AdRequirement.Flexible => await AdService.TryShowInterstitial(sessionId.ToString(), cancellationToken).Vhc(),
            AdRequirement.Rewarded => await AdService.ShowRewarded(sessionId.ToString(), cancellationToken).Vhc(),
            _ => null
        };

        return adResult;
    }

    private async Task AddTcpPacketChannel(CancellationToken cancellationToken)
    {
        // Create and send the Request Message
        var request = new TcpPacketChannelRequest {
            RequestId = UniqueIdFactory.Create(),
            SessionId = SessionId,
            SessionKey = SessionKey
        };

        var requestResult = await SendRequest<SessionResponse>(request, cancellationToken).Vhc();
        StreamPacketChannel? channel = null;
        try {
            // find timespan
            var lifespan = VhUtils.IsInfinite(_maxPacketChannelLifespan)
                ? (TimeSpan?)null
                : TimeSpan.FromSeconds(new Random().Next((int)_minPacketChannelLifespan.TotalSeconds,
                    (int)_maxPacketChannelLifespan.TotalSeconds));

            // PacketChannel should not be reused, otherwise its timespan will be meaningless
            if (lifespan != null)
                requestResult.ClientStream.PreventReuse();

            // add the new channel
            channel = new StreamPacketChannel(new StreamPacketChannelOptions {
                ClientStream = requestResult.ClientStream,
                ChannelId = request.RequestId,
                Blocking = true,
                AutoDisposePackets = true,
                Lifespan = lifespan
            });
            _tunnel.AddChannel(channel);
            _sessionPacketChannelCount++;
        }
        catch {
            channel?.Dispose();
            requestResult.Dispose();
            throw;
        }
    }

    internal async Task<ConnectorRequestResult<T>> SendRequest<T>(ClientRequest request,
        CancellationToken cancellationToken)
        where T : SessionResponse
    {
        if (_disposed)
            throw new ObjectDisposedException(VhLogger.FormatType(this));

        try {
            // create a connection and send the request 
            var requestResult = await ConnectorService.SendRequest<T>(request, cancellationToken).Vhc();
            _sessionStatus?.Update(requestResult.Response.AccessUsage);

            // client is disposed meanwhile
            if (_disposed) {
                requestResult.Dispose();
                throw new ObjectDisposedException(VhLogger.FormatType(this));
            }

            _lastConnectionErrorTime = null;
            if (SessionInfo != null && _vpnAdapter.IsStarted)
                State = ClientState.Connected;
            return requestResult;
        }
        catch (SessionException ex) {
            _sessionStatus?.Update(ex.SessionResponse.AccessUsage);

            // SessionException means that the request accepted by server but there is an error for that request
            _lastConnectionErrorTime = null;

            // close session if server has ended the session
            if (ex.SessionResponse.ErrorCode != SessionErrorCode.GeneralError &&
                ex.SessionResponse.ErrorCode != SessionErrorCode.RedirectHost &&
                ex.SessionResponse.ErrorCode != SessionErrorCode.RewardedAdRejected) {
                await DisposeAsync(ex);
            }

            throw;
        }
        catch (UnauthorizedAccessException ex) {
            await DisposeAsync(ex);
            throw;
        }
        catch (Exception ex) {
            if (_disposed)
                throw;

            var now = FastDateTime.Now;
            _lastConnectionErrorTime ??= now;

            // dispose by session timeout and must before pause because SessionTimeout is bigger than ReconnectTimeout
            if (now - _lastConnectionErrorTime.Value > SessionTimeout)
                await DisposeAsync(ex);

            // pause after retry limit
            else if (now - _lastConnectionErrorTime.Value > ReconnectTimeout) {
                _autoWaitTime = now;
                State = ClientState.Waiting;
                VhLogger.Instance.LogWarning(ex, "Client is paused because of too many connection errors.");
            }

            // set connecting state if it could not establish any connection
            else if (State == ClientState.Connected)
                State = ClientState.Connecting;

            throw;
        }
    }

    public async Task UpdateSessionStatus(CancellationToken cancellationToken)
    {
        // don't use SendRequest because it can be disposed
        using var requestResult = await SendRequest<SessionResponse>(
                new SessionStatusRequest {
                    RequestId = UniqueIdFactory.Create(),
                    SessionId = SessionId,
                    SessionKey = SessionKey
                },
                cancellationToken)
            .Vhc();
    }

    private ValueTask Cleanup(CancellationToken cancellationToken)
    {
        if (FastDateTime.UtcNow > _sessionStatus?.SessionExpirationTime) {
            var ex = new SessionException(SessionErrorCode.AccessExpired);
            VhLogger.Instance.LogError(GeneralEventId.Session, ex, "Session has been expired.");
            return DisposeAsync(ex);
        }

        return default;
    }

    private async ValueTask DisposeAsync(Exception ex)
    {
        // DisposeAsync will try SendByte, and it may cause calling this dispose method again and go to deadlock
        if (_disposed || _disposeLock.IsLocked) // IsLocked means that DisposeAsync is already running
            return;

        LastException = ex;
        await DisposeAsync().Vhc();
    }

    private readonly AsyncLock _disposeLock = new();

    public async ValueTask DisposeAsync()
    {
        using var lockScope = await _disposeLock.LockAsync();
        if (_disposed)
            return;

        // set state to disconnecting
        VhLogger.Instance.LogInformation("Client is getting ready to shutdown...");

        // save the state before disposal
        var shouldSendBye = LastException == null && State == ClientState.Connected;

        // stop adapter and events before sending bye request
        DisposeInternal();

        // dispose async resources
        var byeTimeout = Debugger.IsAttached ? VhUtils.DebuggerTimeout : TunnelDefaults.ByeTimeout;

        // close tracker
        if (_clientUsageTracker != null) {
            using var cts = new CancellationTokenSource(byeTimeout);
            var cancellationToken = cts.Token;
            await VhUtils.TryInvokeAsync(null, () => _clientUsageTracker.Report(cancellationToken)).Vhc();
        }

        // Sending Bye if the session was active before disposal
        if (shouldSendBye) {
            VhLogger.Instance.LogInformation("Sending bye to the server...");
            try {
                // don't use SendRequest because it can be disposed
                using var byteCts = new CancellationTokenSource(byeTimeout);
                using var requestResult = await ConnectorService.SendRequest<SessionResponse>(
                        new ByeRequest {
                            RequestId = UniqueIdFactory.Create(),
                            SessionId = SessionId,
                            SessionKey = SessionKey
                        },
                        byteCts.Token)
                    .Vhc();

                requestResult.ClientStream.DisposeWithoutReuse();
                VhLogger.Instance.LogInformation("Session has been closed on the server successfully.");
            }
            catch (Exception ex) {
                VhLogger.Instance.LogError(GeneralEventId.Session, ex, "Could not send the bye to the server..");
            }

        }

        Dispose();
    }

    private void DisposeInternal()
    {
        lock (_disposeLock) {
            if (_disposedInternal) return;
            _disposedInternal = true;
        }

        // shutdown
        VhLogger.Instance.LogInformation("Client is shutting down...");
        _cancellationTokenSource.TryCancel();
        _cancellationTokenSource.Dispose();

        _cleanupJob.Dispose();
        State = ClientState.Disconnecting;

        // stop reusing tcp connections for faster disposal
        if (_connectorService != null)
            _connectorService.AllowTcpReuse = false;

        // stop processing tunnel & adapter packets
        _vpnAdapter.PacketReceived -= VpnAdapter_PacketReceived;
        _tunnel.PacketReceived -= Tunnel_PacketReceived;
        _clientHost.PacketReceived -= ClientHost_PacketReceived;
        _proxyManager.PacketReceived -= Proxy_PacketReceived;

        VhLogger.Instance.LogDebug("Disposing ClientHost...");
        _clientHost.Dispose();

        // Tunnel
        VhLogger.Instance.LogDebug("Disposing Tunnel...");
        _tunnel.Dispose();

        VhLogger.Instance.LogDebug("Disposing ProxyManager...");
        _proxyManager.Dispose();

        // Make sure async resources are disposed
        _clientUsageTracker?.Dispose();

        // disposing VpnAdapter. It must be at the end of the disposal process so channels can be disposed properly
        // because network change events can cause problems
        if (_autoDisposeVpnAdapter) {
            VhLogger.Instance.LogDebug("Stopping the VpnAdapter...");
            if (_vpnAdapter.IsStarted)
                VhUtils.TryInvoke("Stop the VpnAdapter", () => _vpnAdapter.Stop());
        }
    }

    public void Dispose()
    {
        lock (_disposeLock) {
            if (_disposed) return;
            _disposed = true;
        }

        // dispose all resources before bye request
        DisposeInternal();

        // disposing adapter
        _vpnAdapter.Dispose();

        // dispose ConnectorService
        VhLogger.Instance.LogDebug("Disposing ConnectorService...");
        _connectorService?.Dispose();

        State = ClientState.Disposed; //everything is clean

        // Changing state fire events in a task, so we should not do it immediately after disposing
        // StateChanged = null; 
        VhLogger.Instance.LogInformation("Bye Bye!");
    }

    private class ClientSessionStatus(VpnHoodClient client, AccessUsage accessUsage) : ISessionStatus
    {
        private AccessUsage _accessUsage = accessUsage;
        private readonly Traffic _cycleTraffic = accessUsage.CycleTraffic;
        private readonly Traffic _totalTraffic = accessUsage.TotalTraffic;
        internal void Update(AccessUsage? value) => _accessUsage = value ?? _accessUsage;

        public ClientConnectorStat ConnectorStat => client.ConnectorService.Stat;
        public Traffic Speed => client._tunnel.Speed;
        public Traffic SessionTraffic => client._tunnel.Traffic;
        public Traffic SessionSplitTraffic => client._proxyManager.Traffic;
        public Traffic CycleTraffic => _cycleTraffic + client._tunnel.Traffic;
        public Traffic TotalTraffic => _totalTraffic + client._tunnel.Traffic;
        public int SessionPacketChannelCount => client._sessionPacketChannelCount;
        public int TcpTunnelledCount => client._clientHost.Stat.TcpTunnelledCount;
        public int TcpPassthruCount => client._clientHost.Stat.TcpPassthruCount;
        public int ActivePacketChannelCount => client._tunnel.PacketChannelCount;
        public bool IsUdpMode => client.UseUdpChannel;
        public bool CanExtendByRewardedAd => client.CanExtendByRewardedAd(_accessUsage);
        public long SessionMaxTraffic => _accessUsage.MaxTraffic;
        public DateTime? SessionExpirationTime => _accessUsage.ExpirationTime;
        public int? ActiveClientCount => _accessUsage.ActiveClientCount;
        public AdRequest? AdRequest => client.AdService.AdRequest;
    }

}