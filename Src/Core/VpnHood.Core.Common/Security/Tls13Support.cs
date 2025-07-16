using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace VpnHood.Core.Common.Security;

public static class Tls13Support
{
    public static SslServerAuthenticationOptions GetTls13ServerOptions(X509Certificate2 certificate)
    {
        return new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            ClientCertificateRequired = false,
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                new("h2"),
                new("http/1.1")
            },
            CipherSuitesPolicy = new CipherSuitesPolicy(new[]
            {
                TlsCipherSuite.TLS_AES_256_GCM_SHA384,
                TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
                TlsCipherSuite.TLS_AES_128_GCM_SHA256
            })
        };
    }

    public static SslClientAuthenticationOptions GetTls13ClientOptions()
    {
        return new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                new("h2"),
                new("http/1.1")
            },
            CipherSuitesPolicy = new CipherSuitesPolicy(new[]
            {
                TlsCipherSuite.TLS_AES_256_GCM_SHA384,
                TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
                TlsCipherSuite.TLS_AES_128_GCM_SHA256
            })
        };
    }

    public static bool IsTls13Supported()
    {
        try
        {
            return SslProtocols.Tls13 != 0;
        }
        catch
        {
            return false;
        }
    }
}

public enum TlsCipherSuite
{
    TLS_AES_256_GCM_SHA384 = 0x1302,
    TLS_CHACHA20_POLY1305_SHA256 = 0x1303,
    TLS_AES_128_GCM_SHA256 = 0x1301
}
