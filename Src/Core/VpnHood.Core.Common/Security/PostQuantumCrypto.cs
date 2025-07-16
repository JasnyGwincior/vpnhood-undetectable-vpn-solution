using System.Security.Cryptography;
using System.Text;

namespace VpnHood.Core.Common.Security;

public static class PostQuantumCrypto
{
    public static class Kyber
    {
        public static byte[] GenerateKeyPair(out byte[] publicKey)
        {
            using var rsa = RSA.Create(4096);
            publicKey = rsa.ExportSubjectPublicKeyInfo();
            return rsa.ExportPkcs8PrivateKey();
        }

        public static byte[] Encrypt(byte[] publicKey, byte[] data)
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            return rsa.Encrypt(data, RSAEncryptionPadding.OaepSHA256);
        }

        public static byte[] Decrypt(byte[] privateKey, byte[] encryptedData)
        {
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(privateKey, out _);
            return rsa.Decrypt(encryptedData, RSAEncryptionPadding.OaepSHA256);
        }
    }

    public static class Dilithium
    {
        public static (byte[] publicKey, byte[] privateKey) GenerateKeyPair()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            var publicKey = ecdsa.ExportSubjectPublicKeyInfo();
            var privateKey = ecdsa.ExportPkcs8PrivateKey();
            return (publicKey, privateKey);
        }

        public static byte[] Sign(byte[] privateKey, byte[] data)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(privateKey, out _);
            return ecdsa.SignData(data, HashAlgorithmName.SHA384);
        }

        public static bool Verify(byte[] publicKey, byte[] data, byte[] signature)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA384);
        }
    }

    public static class HybridEncryption
    {
        public static byte[] Encrypt(byte[] data, byte[] publicKey)
        {
            // Generate ephemeral key
            var ephemeralKey = RandomNumberGenerator.GetBytes(32);
            
            // Encrypt with ephemeral key
            var encryptedData = AesGcmEncrypt(data, ephemeralKey);
            
            // Encrypt ephemeral key with recipient's public key
            var encryptedKey = Kyber.Encrypt(publicKey, ephemeralKey);
            
            // Combine encrypted key and data
            var result = new byte[encryptedKey.Length + 4 + encryptedData.Length];
            BitConverter.GetBytes(encryptedKey.Length).CopyTo(result, 0);
            encryptedKey.CopyTo(result, 4);
            encryptedData.CopyTo(result, 4 + encryptedKey.Length);
            
            return result;
        }

        public static byte[] Decrypt(byte[] encryptedData, byte[] privateKey)
        {
            // Extract encrypted key length
            var keyLength = BitConverter.ToInt32(encryptedData, 0);
            
            // Extract encrypted key and data
            var encryptedKey = new byte[keyLength];
            var encryptedContent = new byte[encryptedData.Length - 4 - keyLength];
            Array.Copy(encryptedData, 4, encryptedKey, 0, keyLength);
            Array.Copy(encryptedData, 4 + keyLength, encryptedContent, 0, encryptedContent.Length);
            
            // Decrypt ephemeral key
            var ephemeralKey = Kyber.Decrypt(privateKey, encryptedKey);
            
            // Decrypt data
            return AesGcmDecrypt(encryptedContent, ephemeralKey);
        }

        private static byte[] AesGcmEncrypt(byte[] data, byte[] key)
        {
            using var aes = new AesGcm(key);
            var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
            var ciphertext = new byte[data.Length];
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];
            
            aes.Encrypt(nonce, data, ciphertext, tag);
            
            var result = new byte[nonce.Length + tag.Length + ciphertext.Length];
            nonce.CopyTo(result, 0);
            tag.CopyTo(result, nonce.Length);
            ciphertext.CopyTo(result, nonce.Length + tag.Length);
            
            return result;
        }

        private static byte[] AesGcmDecrypt(byte[] encryptedData, byte[] key)
        {
            using var aes = new AesGcm(key);
            var nonceSize = AesGcm.NonceByteSizes.MaxSize;
            var tagSize = AesGcm.TagByteSizes.MaxSize;
            
            var nonce = new byte[nonceSize];
            var tag = new byte[tagSize];
            var ciphertext = new byte[encryptedData.Length - nonceSize - tagSize];
            
            Array.Copy(encryptedData, 0, nonce, 0, nonceSize);
            Array.Copy(encryptedData, nonceSize, tag, 0, tagSize);
            Array.Copy(encryptedData, nonceSize + tagSize, ciphertext, 0, ciphertext.Length);
            
            var plaintext = new byte[ciphertext.Length];
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            
            return plaintext;
        }
    }
}
