﻿using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VpnHood.Core.Packets;
using VpnHood.Core.Packets.Extensions;
using VpnHood.Core.Toolkit.Exceptions;
using VpnHood.Core.Toolkit.Logging;
using VpnHood.Core.Toolkit.Net;
using VpnHood.Core.Toolkit.Utils;
using VpnHood.Core.VpnAdapters.Abstractions;
using VpnHood.Core.VpnAdapters.WinTun.WinNative;

namespace VpnHood.Core.VpnAdapters.WinTun;

public class WinTunVpnAdapter(WinVpnAdapterSettings adapterSettings)
    : TunVpnAdapter(adapterSettings)
{
    private readonly int _ringCapacity = adapterSettings.RingCapacity;
    private IntPtr _tunAdapter;
    private IntPtr _tunSession;
    private IntPtr _readEvent;
    private readonly byte[] _writeBuffer = new byte[0xFFFF];

    public const int MinRingCapacity = 0x20000; // 128kiB
    public const int MaxRingCapacity = 0x4000000; // 64MiB
    protected override bool IsSocketProtectedByBind => true;
    public override bool IsNatSupported => true;
    public override bool IsAppFilterSupported => false;
    protected override string? AppPackageId => null;
    protected override Task SetAllowedApps(string[] packageIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException("App filtering is not supported on WinTun.");

    protected override Task SetDisallowedApps(string[] packageIds, CancellationToken cancellationToken) =>
        throw new NotSupportedException("App filtering is not supported on WinTun.");

    private static Guid BuildGuidFromName(string adapterName)
    {
        adapterName = $"VpnHood.{adapterName}"; // make sure it is unique
        using var sha1 = SHA1.Create();
        var hashBytes = sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(adapterName));

        // Create 16 bytes array for GUID
        var guidBytes = new byte[16];
        Array.Copy(hashBytes, 0, guidBytes, 0, 16);

        // Set UUID version (5: SHA-1-based name-based UUID)
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x50); // set version to 5
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80); // set variant to RFC 4122

        var adapterGuid = new Guid(guidBytes);
        return adapterGuid;
    }

    //public static int GetAdapterIndex(Guid adapterId)
    //{
    //    var adapter = NetworkInterface.GetAllNetworkInterfaces()
    //        .Single(x => Guid.TryParse(x.Id, out var id) && id == adapterId);

    //    var ipProps = adapter.GetIPProperties();
    //    var index = ipProps.GetIPv4Properties()?.Index ?? -1;
    //    return index;
    //}

    protected override Task AdapterAdd(CancellationToken cancellationToken)
    {
        // Load the WinTun DLL
        LoadWinTunDll();

        // create the adapter
        _tunAdapter = WinTunApi.WintunCreateAdapter(AdapterName, "VPN", BuildGuidFromName(AdapterName));

        // close the adapter if it was already exists error 183 (ERROR_ALREADY_EXISTS)
        if (_tunAdapter == IntPtr.Zero && Marshal.GetLastWin32Error() == 183) {
            var prevAdapter = WinTunApi.WintunOpenAdapter(AdapterName);
            if (prevAdapter != IntPtr.Zero)
                WinTunApi.WintunCloseAdapter(prevAdapter);

            // try to create the adapter again
            _tunAdapter = WinTunApi.WintunCreateAdapter(AdapterName, "VPN", BuildGuidFromName(AdapterName));
        }

        if (_tunAdapter == IntPtr.Zero)
            throw new PInvokeException("Failed to create WinTun adapter. Make sure the app is running with admin privilege.");

        return Task.CompletedTask;
    }

    protected override void AdapterRemove()
    {
        // close the adapter
        if (_tunAdapter != IntPtr.Zero) {
            WinTunApi.WintunCloseAdapter(_tunAdapter);
            _tunAdapter = IntPtr.Zero;
        }

        // Remove previous NAT iptables record
        if (UseNat) {
            VhLogger.Instance.LogDebug("Removing previous NAT iptables record for {AdapterName} TUN adapter...", AdapterName);
            if (AdapterIpNetworkV4 != null)
                TryRemoveNat(AdapterIpNetworkV4);

            if (AdapterIpNetworkV6 != null)
                TryRemoveNat(AdapterIpNetworkV6);
        }
    }

    protected override Task AdapterOpen(CancellationToken cancellationToken)
    {

        // start WinTun session
        VhLogger.Instance.LogInformation("Starting WinTun session...");
        _tunSession = WinTunApi.WintunStartSession(_tunAdapter, _ringCapacity);
        if (_tunSession == IntPtr.Zero)
            throw new Win32Exception("Failed to start WinTun session.");

        // create an event object to wait for packets
        VhLogger.Instance.LogDebug("Creating event object for WinTun...");
        _readEvent = WinTunApi.WintunGetReadWaitEvent(_tunSession); // do not close this handle by documentation

        return Task.CompletedTask;
    }

    protected override void AdapterClose()
    {
        if (_tunSession != IntPtr.Zero) {
            WinTunApi.WintunEndSession(_tunSession);
            _tunSession = IntPtr.Zero;
        }

        // do not close this handle by documentation
        _readEvent = IntPtr.Zero;
    }

    protected override Task SetSessionName(string sessionName, CancellationToken cancellationToken)
    {
        // not supported. ignore
        return Task.CompletedTask;
    }

    protected override async Task SetMetric(int metric, bool ipV4, bool ipV6, CancellationToken cancellationToken)
    {
        if (ipV4)
            await OsUtils.ExecuteCommandAsync("netsh", $"interface ipv4 set interface \"{AdapterName}\" metric={metric}", cancellationToken);

        if (ipV6)
            await OsUtils.ExecuteCommandAsync("netsh", $"interface ipv6 set interface \"{AdapterName}\" metric={metric}", cancellationToken);
    }

    protected override async Task AddAddress(IpNetwork ipNetwork, CancellationToken cancellationToken)
    {
        var command = ipNetwork.IsV4
            ? $"interface ipv4 set address \"{AdapterName}\" static {ipNetwork}"
            : $"interface ipv6 set address \"{AdapterName}\" {ipNetwork}";

        await OsUtils.ExecuteCommandAsync("netsh", command, cancellationToken);
    }

    private async Task AddRouteUsingNetsh(IpNetwork ipNetwork, CancellationToken cancellationToken)
    {
        var command = ipNetwork.IsV4
            ? $"interface ipv4 add route {ipNetwork} \"{AdapterName}\""
            : $"interface ipv6 add route {ipNetwork} \"{AdapterName}\"";

        await OsUtils.ExecuteCommandAsync("netsh", command, cancellationToken);
    }

    protected override Task AddRoute(IpNetwork ipNetwork, CancellationToken cancellationToken)
    {
        return AddRouteUsingNetsh(ipNetwork, cancellationToken);
    }

    // ReSharper disable once UnusedMember.Local
    private uint GetAdapterIndex()
    {
        var networkInterface = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => x.Name == AdapterName);
        if (networkInterface == null)
            throw new InvalidOperationException($"Could not find network adapter with name '{AdapterName}'.");

        // Get the index of the adapter
        if (networkInterface.Supports(NetworkInterfaceComponent.IPv4)) {
            var index = (uint)networkInterface.GetIPProperties().GetIPv4Properties().Index;
            if (index != 0)
                return index; // Return the index if it is valid (not 0)
        }

        if (networkInterface.Supports(NetworkInterfaceComponent.IPv6)) {
            var index = (uint)networkInterface.GetIPProperties().GetIPv6Properties().Index;
            if (index != 0)
                return index; // Return the index if it is valid (not 0)
        }

        // If neither IPv4 nor IPv6 is supported, throw an exception
        throw new InvalidOperationException($"Adapter '{AdapterName}' does not support IPv4 or IPv6.");
    }

    protected override async Task SetMtu(int mtu, bool ipV4, bool ipV6, CancellationToken cancellationToken)
    {
        if (ipV4)
            await OsUtils.ExecuteCommandAsync("netsh", $"interface ipv4 set subinterface \"{AdapterName}\" mtu={mtu}", cancellationToken);

        if (ipV6)
            await OsUtils.ExecuteCommandAsync("netsh", $"interface ipv6 set subinterface \"{AdapterName}\" mtu={mtu}", cancellationToken);
    }


    //protected override async Task RemoveAllDnsServers(CancellationToken cancellationToken)
    //{
    //    var commandV4 = $"interface ipv4 set dns \"{AdapterName}\" dhcp";
    //    var commandV6 = $"interface ipv6 set dns \"{AdapterName}\" dhcp";

    //    await OsUtils.ExecuteCommandAsync("netsh", commandV4, cancellationToken);
    //    await OsUtils.ExecuteCommandAsync("netsh", commandV6, cancellationToken);
    //}

    protected override async Task SetDnsServers(IPAddress[] dnsServers, CancellationToken cancellationToken)
    {
        // remove previous DNS servers.
        // Do not log in debug mode because it is common error as the adapter is usually new
        VhLogger.Instance.LogDebug("Removing previous DNS from the adapter...");
        await VhUtils.TryInvokeAsync(VhLogger.MinLogLevel == LogLevel.Trace ? "Remove previous DNS" : "",
            () => OsUtils.ExecuteCommandAsync("netsh", $"netsh interface ipv4 delete dns \"{AdapterName}\" all", cancellationToken));

        VhLogger.Instance.LogDebug("Adding new DNS to the adapter...");
        foreach (var ipAddress in dnsServers) {
            var command = ipAddress.IsV4()
                ? $"interface ipv4 add dns \"{AdapterName}\" {ipAddress}"
                : $"interface ipv6 add dns \"{AdapterName}\" {ipAddress}";

            await OsUtils.ExecuteCommandAsync("netsh", command, cancellationToken);
        }
    }

    protected override async Task AddNat(IpNetwork ipNetwork, CancellationToken cancellationToken)
    {
        // Remove previous NAT if any
        TryRemoveNat(ipNetwork);

        // Configure NAT with iptables
        if (ipNetwork.IsV4) {
            // let's throw error in ipv4
            var natName = $"{AdapterName}Nat";
            await ExecutePowerShellCommandAsync($"New-NetNat -Name {natName} -InternalIPInterfaceAddressPrefix {ipNetwork}",
                cancellationToken).Vhc();
        }

        if (ipNetwork.IsV6) {
            var natName = $"{AdapterName}NatIpV6";

            // ignore exception in ipv6 on windows
            await VhUtils.TryInvokeAsync("Configuring NAT for IPv6", () =>
                ExecutePowerShellCommandAsync($"New-NetNat -Name {natName} -InternalIPInterfaceAddressPrefix {ipNetwork}",
                    cancellationToken));
        }
    }

    private static void TryRemoveNat(IpNetwork ipNetwork)
    {
        // Remove NAT rule. try until no rule found
        VhUtils.TryInvoke("Remove NAT rule", () =>
            ExecutePowerShellCommand(
                $"Get-NetNat | Where-Object {{ $_.InternalIPInterfaceAddressPrefix -eq '{ipNetwork}' }} | Remove-NetNat -Confirm:$false"));
    }

    private static Task<string> ExecutePowerShellCommandAsync(string command, CancellationToken cancellationToken)
    {
        var ps = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"";
        return OsUtils.ExecuteCommandAsync("powershell.exe", ps, cancellationToken);
    }

    private static string ExecutePowerShellCommand(string command)
    {
        var ps = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"";
        return OsUtils.ExecuteCommand("powershell.exe", ps);
    }

    protected override void WaitForTunRead()
    {
        var result = Kernel32.WaitForSingleObject(_readEvent, Kernel32.Infinite);
        if (result == Kernel32.WaitObject0)
            return;

        throw result == Kernel32.WaitFailed
            ? new Win32Exception()
            : new PInvokeException("Unexpected result from WaitForSingleObject", (int)result);
    }


    protected override bool ReadPacket(byte[] buffer)
    {
        var tunReceivePacket = WinTunApi.WintunReceivePacket(_tunSession, out var size);

        // return if something is written
        if (tunReceivePacket != IntPtr.Zero) {
            try {
                Marshal.Copy(tunReceivePacket, buffer, 0, size);
                return true;
            }
            finally {
                WinTunApi.WintunReleaseReceivePacket(_tunSession, tunReceivePacket);
            }
        }

        // check  for errors
        var lastError = (WintunReceivePacketError)Marshal.GetLastWin32Error();
        return lastError switch {
            WintunReceivePacketError.NoMoreItems => false,
            WintunReceivePacketError.HandleEof => throw new IOException("WinTun adapter has been closed."),
            WintunReceivePacketError.InvalidData => throw new InvalidOperationException(
                "Invalid data received from WinTun adapter."),
            _ => throw new PInvokeException(
                $"Unknown error in reading packet from WinTun. LastError: {lastError}")
        };
    }

    protected override void WaitForTunWrite()
    {
        Thread.Sleep(1);
    }

    protected override bool WritePacket(IpPacket ipPacket)
    {
        var packetBytes = ipPacket.Buffer;

        // Allocate memory for the packet inside WinTun ring buffer
        var packetMemory = WinTunApi.WintunAllocateSendPacket(_tunSession, packetBytes.Length); // thread-safe
        if (packetMemory == IntPtr.Zero)
            return false;


        // Copy the raw packet data into WinTun memory
        var buffer = ipPacket.GetUnderlyingBufferUnsafe(_writeBuffer, out var offset, out var length);
        Marshal.Copy(buffer, offset, packetMemory, length);

        // Send the packet through WinTun
        WinTunApi.WintunSendPacket(_tunSession, packetMemory); // thread-safe
        return true;
    }

    // Note: System may load driver into memory and lock it, so we'd better to copy it into a temporary folder 
    private static void LoadWinTunDll()
    {
        var isWin = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var cpuArchitecture = RuntimeInformation.OSArchitecture switch {
            Architecture.Arm64 when isWin => "arm64",
            Architecture.X64 when isWin => "x64",
            _ => throw new NotSupportedException("WinTun is not supported on this OS.")
        };

        var destinationFolder = Path.Combine(Path.GetTempPath(), "VpnHood", "WinTun", "0.14.1");
        var requiredFiles = new[] { Path.Combine("bin", cpuArchitecture, "wintun.dll") };
        var checkFiles = requiredFiles.Select(x => Path.Combine(destinationFolder, x));
        if (checkFiles.Any(x => !File.Exists(x))) {
            using var memStream = new MemoryStream(Resources.WinTunZip);
            using var zipArchive = new ZipArchive(memStream);
            zipArchive.ExtractToDirectory(destinationFolder, true);
        }

        // Load the DLL
        var dllFile = Path.Combine(destinationFolder, "bin", cpuArchitecture, "wintun.dll");
        if (Kernel32.LoadLibrary(dllFile) == IntPtr.Zero)
            throw new Win32Exception("Failed to load WinTun DLL.");
    }

    protected override void DisposeUnmanaged()
    {
        // The adapter is an unmanaged resource; it must be closed if it is open
        if (_tunAdapter != IntPtr.Zero)
            AdapterRemove();

        base.DisposeUnmanaged();
    }

    ~WinTunVpnAdapter()
    {
        Dispose(false);
    }

}