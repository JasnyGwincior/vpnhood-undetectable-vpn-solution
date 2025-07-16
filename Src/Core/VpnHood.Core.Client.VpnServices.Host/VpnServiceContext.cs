﻿using System.Text.Json;
using Microsoft.Extensions.Logging;
using VpnHood.Core.Client.Abstractions;
using VpnHood.Core.Client.VpnServices.Abstractions;
using VpnHood.Core.Toolkit.Logging;
using VpnHood.Core.Toolkit.Utils;

namespace VpnHood.Core.Client.VpnServices.Host;

internal class VpnServiceContext(string configFolder)
{
    public string ConfigFilePath => Path.Combine(configFolder, ClientOptions.VpnConfigFileName);
    public string StatusFilePath => Path.Combine(configFolder, ClientOptions.VpnStatusFileName);
    public string LogFilePath => Path.Combine(configFolder, ClientOptions.VpnLogFileName);
    public string ConfigFolder => configFolder;

    public ConnectionInfo ConnectionInfo { get; private set; } = new() {
        ApiEndPoint = null,
        ApiKey = null,
        ClientState = ClientState.Initializing,
        CreatedTime = FastDateTime.Now,
        Error = null,
        SessionInfo = null,
        SessionName = null,
        SessionStatus = null
    };

    public ClientOptions? TryReadClientOptions()
    {
        try {
            return ReadClientOptions();
        }
        catch (Exception ex) {
            VhLogger.Instance.LogError(ex, "Could not read client options from file.");
            return null;
        }
    }

    public ClientOptions ReadClientOptions()
    {
        // read from config file
        var json = File.ReadAllText(ConfigFilePath);
        return JsonUtils.Deserialize<ClientOptions>(json);
    }

    private readonly AsyncLock _connectionInfoLock = new();
    public async Task<bool> TryWriteConnectionInfo(ConnectionInfo connectionInfo, CancellationToken cancellationToken)
    {
        try {
            using var scopeLock = await _connectionInfoLock.LockAsync(cancellationToken);
            ConnectionInfo = connectionInfo;
            var json = JsonSerializer.Serialize(connectionInfo);
            await FileUtils.WriteAllTextRetryAsync(StatusFilePath, json, timeout: TimeSpan.FromSeconds(2),
                cancellationToken: cancellationToken);
            return true;
        }
        catch (OperationCanceledException) {
            return false; // operation was cancelled
        }
        catch (Exception ex) {
            VhLogger.Instance.LogError(ex, "Could not save connection info to file. FilePath: {FilePath}", StatusFilePath);
            return false;
        }
    }
}