﻿using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using VpnHood.Core.Common.Messaging;
using VpnHood.Core.Server.Access.Managers.FileAccessManagement.Dtos;
using VpnHood.Core.Toolkit.Logging;
using VpnHood.Core.Toolkit.Utils;

namespace VpnHood.Core.Server.Access.Managers.FileAccessManagement.Services;

public class AccessTokenService
{
    private const string FileExtAccessToken = ".token2";
    private const string FileExtAccessTokenUsage = ".usage";
    private readonly ConcurrentDictionary<string, AccessTokenData> _items = new();
    private readonly string _storagePath;

    public AccessTokenService(string storagePath)
    {
        _storagePath = storagePath;
        AccessTokenLegacyConverter.ConvertToken1ToToken2(storagePath, FileExtAccessToken);
    }

    private string GetAccessTokenFileName(string tokenId)
    {
        // check is tokenId has any invalid file character
        if (tokenId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("invalid character int token id.");

        return Path.Combine(_storagePath, tokenId + FileExtAccessToken);
    }

    private string GetAccessTokenUsageFileName(string tokenId)
    {
        // check is tokenId has any invalid file character
        if (tokenId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("invalid character int token id.");

        return Path.Combine(_storagePath, tokenId + FileExtAccessTokenUsage);
    }

    public AccessToken Create(
        int maxClientCount = 1,
        string? tokenName = null,
        int maxTrafficByteCount = 0,
        DateTime? expirationTime = null,
        AdRequirement adRequirement = AdRequirement.None)
    {
        // generate key
        var aes = Aes.Create();
        aes.KeySize = 128;
        aes.GenerateKey();

        // create AccessToken
        var accessToken = new AccessToken {
            TokenId = Guid.NewGuid().ToString(),
            IssuedAt = DateTime.UtcNow,
            MaxTraffic = maxTrafficByteCount,
            MaxClientCount = maxClientCount,
            ExpirationTime = expirationTime,
            AdRequirement = adRequirement,
            Secret = aes.Key,
            Name = tokenName
        };

        // Write AccessToken
        File.WriteAllText(GetAccessTokenFileName(accessToken.TokenId), JsonSerializer.Serialize(accessToken));

        return accessToken;
    }

    public async Task<AccessTokenData[]> List()
    {
        var files = Directory.GetFiles(_storagePath, "*" + FileExtAccessToken);
        var tokenItems = new List<AccessTokenData>();

        foreach (var file in files) {
            var tokenItem = await Find(Path.GetFileNameWithoutExtension(file)).Vhc();
            if (tokenItem != null)
                tokenItems.Add(tokenItem);
        }

        return tokenItems.ToArray();
    }

    public Task<int> GetTotalCount()
    {
        var files = Directory.GetFiles(_storagePath, "*" + FileExtAccessToken);
        return Task.FromResult(files.Length);
    }

    public async Task<AccessTokenData> Get(string tokenId)
    {
        using var tokenLock = await AsyncLock.LockAsync(GetTokenLockName(tokenId)).Vhc();
        return await GetInternal(tokenId);
    }


    private async Task<AccessTokenData> GetInternal(string tokenId)
    {
        // try get from cache
        if (_items.TryGetValue(tokenId, out var accessTokenData))
            return accessTokenData;

        // read access token record
        var tokenFileName = GetAccessTokenFileName(tokenId);
        if (!File.Exists(tokenFileName))
            throw new KeyNotFoundException($"Could not find tokenId. TokenId: {tokenId}");

        // try read token
        var tokenJson = await File.ReadAllTextAsync(tokenFileName).Vhc();
        var accessToken = JsonUtils.Deserialize<AccessToken>(tokenJson);

        // try read usage
        var usageFileName = GetAccessTokenUsageFileName(tokenId);
        var usage = JsonUtils.TryDeserializeFile<AccessTokenUsage>(usageFileName) ??
                    new AccessTokenUsage { Version = 2 };

        // for backward compatibility
        if (File.Exists(usageFileName) && usage.Version < 2) {
            usage.CreatedTime = File.GetCreationTimeUtc(usageFileName);
            usage.LastUsedTime = File.GetLastWriteTime(usageFileName);
        }

        // create access token data
        accessTokenData = new AccessTokenData {
            AccessToken = accessToken,
            Usage = usage
        };

        // add to cache
        _items[tokenId] = accessTokenData;
        return accessTokenData;
    }

    public async Task<AccessTokenData> Update(string tokenId, string tokenName)
    {
        using var tokenLock = await AsyncLock.LockAsync(GetTokenLockName(tokenId)).Vhc();

        var accessTokenData = await GetInternal(tokenId);
        accessTokenData.AccessToken.Name = tokenName;
        await File
            .WriteAllTextAsync(GetAccessTokenUsageFileName(tokenId), JsonSerializer.Serialize(accessTokenData.Usage))
            .Vhc();

        return accessTokenData;
    }


    public async Task<AccessTokenData?> Find(string tokenId)
    {
        try {
            return await Get(tokenId).Vhc();
        }
        catch (Exception ex) {
            VhLogger.Instance.LogError(ex, "Failed to get token item. TokenId: {TokenId}", tokenId);
            return null;
        }
    }

    private static string GetTokenLockName(string tokenId) => $"token_{tokenId}";

    public async Task AddUsage(string tokenId, Traffic traffic)
    {
        //lock tokenId
        using var tokenLock = await AsyncLock.LockAsync(GetTokenLockName(tokenId)).Vhc();

        // add usage
        var accessTokenData = await GetInternal(tokenId).Vhc();
        accessTokenData.Usage.Sent += traffic.Sent;
        accessTokenData.Usage.Received += traffic.Received;
        accessTokenData.Usage.Version = 2;
        accessTokenData.Usage.LastUsedTime = DateTime.UtcNow;

        // save to file
        await File
            .WriteAllTextAsync(GetAccessTokenUsageFileName(tokenId), JsonSerializer.Serialize(accessTokenData.Usage))
            .Vhc();
    }

    public async Task Delete(string tokenId)
    {
        // validate is it exists
        _ = await Find(tokenId).Vhc()
            ?? throw new KeyNotFoundException("Could not find tokenId");

        // delete files
        using var tokenLock = await AsyncLock.LockAsync(GetTokenLockName(tokenId)).Vhc();
        
        // delete from cache
        _items.TryRemove(tokenId, out _);

        if (File.Exists(GetAccessTokenUsageFileName(tokenId)))
            File.Delete(GetAccessTokenUsageFileName(tokenId));

        if (File.Exists(GetAccessTokenFileName(tokenId)))
            File.Delete(GetAccessTokenFileName(tokenId));
    }
}