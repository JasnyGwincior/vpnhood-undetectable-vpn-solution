# VpnHood Enhancement Integration Examples

## Quick Start Guide

### 1. Memory Optimization Integration

```csharp
// In your VpnHoodApp initialization
using VpnHood.Core.Common.Performance;
using VpnHood.Core.Common.Pooling;

public class EnhancedVpnHoodApp
{
    private MemoryPressureMonitor? _memoryMonitor;
    private HealthCheckService? _healthService;
    
    public void Initialize()
    {
        // Enable memory monitoring
        _memoryMonitor = new MemoryPressureMonitor(logger);
        _memoryMonitor.MemoryPressureDetected += OnMemoryPressureDetected;
        
        // Enable health monitoring
        _healthService = new HealthCheckService(logger);
        _healthService.RegisterHealthCheck(new MemoryHealthCheck());
        _healthService.RegisterHealthCheck(new NetworkHealthCheck());
    }
    
    private void OnMemoryPressureDetected(object? sender, MemoryPressureEventArgs e)
    {
        logger.LogWarning("Memory pressure detected: {MemoryInfo}", e.MemoryInfo);
        // Trigger garbage collection
        GC.Collect();
    }
}
```

### 2. Security Enhancements

```csharp
// Post-quantum cryptography integration
using VpnHood.Core.Common.Security;

public class SecureConnectionManager
{
    public async Task<byte[]> EncryptData(byte[] data, byte[] publicKey)
    {
        // Use hybrid encryption (classical + post-quantum)
        return PostQuantumCrypto.HybridEncryption.Encrypt(data, publicKey);
    }
    
    public async Task<byte[]> DecryptData(byte[] encryptedData, byte[] privateKey)
    {
        return PostQuantumCrypto.HybridEncryption.Decrypt(encryptedData, privateKey);
    }
}
```

### 3. DNS Leak Protection

```csharp
// DNS leak protection integration
using VpnHood.Core.Common.Security;

public class SecureDnsManager
{
    private readonly DnsLeakProtection _dnsProtection;
    
    public SecureDnsManager(ILogger logger)
    {
        _dnsProtection = new DnsLeakProtection(logger);
    }
    
    public async Task<bool> ValidateDnsConfiguration(IPAddress vpnDnsServer)
    {
        var hasLeaks = await _dnsProtection.TestForDnsLeaksAsync(vpnDnsServer);
        if (hasLeaks)
        {
            // Configure secure DNS
            _dnsProtection.ConfigureSecureDns(vpnDnsServer);
        }
        return !hasLeaks;
    }
}
```

### 4. Connection Optimization

```csharp
// Network optimization integration
using VpnHood.Core.Common.Performance;

public class OptimizedConnectionManager
{
    private readonly ConnectionOptimizer _optimizer;
    
    public OptimizedConnectionManager(ILogger logger)
    {
        _optimizer = new ConnectionOptimizer(logger);
        _optimizer.OptimizeTcpSettings();
    }
    
    public async Task<int> GetOptimalMtu(IPAddress targetServer)
    {
        return await _optimizer.DiscoverOptimalMtuAsync(targetServer);
    }
}
```

### 5. TLS 1.3 Integration

```csharp
// TLS 1.3 support integration
using VpnHood.Core.Common.Security;

public class Tls13Connection
{
    public SslServerAuthenticationOptions GetEnhancedTlsOptions(X509Certificate2 certificate)
    {
        return Tls13Support.GetTls13ServerOptions(certificate);
    }
}
```

## Server Configuration Updates

### appsettings.json Enhancements
```json
{
  "Performance": {
    "EnableMemoryPooling": true,
    "EnableConnectionOptimization": true,
    "EnableTls13": true,
    "MaxBufferPoolSize": 1000,
    "MemoryPressureThreshold": "1GB"
  },
  "Security": {
    "EnablePostQuantumCrypto": true,
    "EnableDnsLeakProtection": true,
    "SecureDnsServers": ["1.1.1.1", "8.8.8.8", "9.9.9.9"]
  },
  "Monitoring": {
    "EnableHealthChecks": true,
    "HealthCheckInterval": "00:01:00",
    "EnableMemoryMonitoring": true,
    "MemoryCheckInterval": "00:00:30"
  }
}
```

## Client Integration

### Enhanced Client Initialization
```csharp
public class EnhancedVpnHoodClient
{
    private readonly VpnHoodApp _app;
    private readonly MemoryPressureMonitor _memoryMonitor;
    private readonly HealthCheckService _healthService;
    private readonly DnsLeakProtection _dnsProtection;
    
    public EnhancedVpnHoodClient(AppOptions options)
    {
        _app = VpnHoodApp.Init(device, options);
        
        // Initialize enhancements
        _memoryMonitor = new MemoryPressureMonitor(_app.Logger);
        _healthService = new HealthCheckService(_app.Logger);
        _dnsProtection = new DnsLeakProtection(_app.Logger);
        
        // Register health checks
        _healthService.RegisterHealthCheck(new MemoryHealthCheck());
        _healthService.RegisterHealthCheck(new NetworkHealthCheck());
        
        // Configure DNS protection
        _ = ConfigureDnsProtectionAsync();
    }
    
    private async Task ConfigureDnsProtectionAsync()
    {
        var clientProfile = _app.CurrentClientProfileInfo;
        if (clientProfile?.Token?.ServerToken?.DnsServers != null)
        {
            foreach (var dnsServer in clientProfile.Token.ServerToken.DnsServers)
            {
                await _dnsProtection.ValidateDnsConfiguration(dnsServer);
            }
        }
    }
}
```

## Performance Monitoring

### Real-time Metrics Collection
```csharp
public class PerformanceMetricsCollector
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, long> _metrics = new();
    
    public void CollectMetrics()
    {
        // Memory metrics
        _metrics["WorkingSet"] = Process.GetCurrentProcess().WorkingSet64;
        _metrics["GCMemory"] = GC.GetTotalMemory(false);
        
        // Network metrics
        _metrics["ActiveConnections"] = GetActiveConnectionCount();
        _metrics["BytesTransferred"] = GetBytesTransferred();
        
        _logger.LogInformation("Performance metrics: {Metrics}", _metrics);
    }
}
```

## Testing Examples

### Unit Tests for Enhancements
```csharp
[TestClass]
public class EnhancementTests
{
    [TestMethod]
    public void TestBufferPool()
    {
        var buffer = BufferPool.Rent(1024);
        Assert.IsNotNull(buffer);
        Assert.IsTrue(buffer.Length >= 1024);
        BufferPool.Return(buffer);
    }
    
    [TestMethod]
    public async Task TestDnsLeakProtection()
    {
        var logger = new TestLogger();
        var dnsProtection = new DnsLeakProtection(logger);
        
        var hasLeaks = await dnsProtection.TestForDnsLeaksAsync(IPAddress.Parse("1.1.1.1"));
        Assert.IsFalse(hasLeaks); // Should not leak to secure DNS
    }
    
    [TestMethod]
    public async Task TestConnectionOptimizer()
    {
        var logger = new TestLogger();
        var optimizer = new ConnectionOptimizer(logger);
        
        var mtu = await optimizer.DiscoverOptimalMtuAsync(IPAddress.Parse("8.8.8.8"));
        Assert.IsTrue(mtu >= 576 && mtu <= 9000);
    }
}
```

## Deployment Scripts

### Docker with Enhancements
```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Src/Core/VpnHood.Core.Common/VpnHood.Core.Common.csproj", "Src/Core/VpnHood.Core.Common/"]
RUN dotnet restore "Src/Core/VpnHood.Core.Common/VpnHood.Core.Common.csproj"

COPY . .
WORKDIR "/src/Src/Core/VpnHood.Core.Common"
RUN dotnet build "VpnHood.Core.Common.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "VpnHood.Core.Common.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "VpnHood.Core.Common.dll"]
```

### Kubernetes Deployment
```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: vpnhood-config
data:
  appsettings.json: |
    {
      "Performance": {
        "EnableMemoryPooling": true,
        "EnableConnectionOptimization": true
      },
      "Security": {
        "EnablePostQuantumCrypto": true,
        "EnableDnsLeakProtection": true
      }
    }
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: vpnhood-enhanced
spec:
  replicas: 3
  selector:
    matchLabels:
      app: vpnhood-enhanced
  template:
    metadata:
      labels:
        app: vpnhood-enhanced
    spec:
      containers:
      - name: vpnhood
        image: vpnhood/enhanced:latest
        ports:
        - containerPort: 443
        env:
        - name: ASPNETCORE_ENVIRONMENT
          value: Production
        volumeMounts:
        - name: config
          mountPath: /app/config
        resources:
          requests:
            memory: "256Mi"
            cpu: "250m"
          limits:
            memory: "512Mi"
            cpu: "500m"
      volumes:
      - name: config
        configMap:
          name: vpnhood-config
```

## Troubleshooting

### Common Issues and Solutions

1. **Memory Pressure**
   - Check MemoryPressureMonitor logs
   - Adjust buffer pool sizes
   - Enable automatic GC optimization

2. **DNS Leaks**
   - Verify DNS configuration
   - Check firewall rules
   - Test with multiple DNS servers

3. **Connection Issues**
   - Run MTU discovery
   - Check TLS 1.3 support
   - Validate certificate configuration

### Debug Commands
```bash
# Check memory usage
dotnet run -- check-memory

# Test DNS leaks
dotnet run -- test-dns-leaks

# Optimize connections
dotnet run -- optimize-connections

# Run health checks
dotnet run -- health-check
```

## Support

For technical support with these enhancements:
1. Review the comprehensive documentation
2. Check the troubleshooting guide
3. Submit issues to GitHub
4. Join community discussions
