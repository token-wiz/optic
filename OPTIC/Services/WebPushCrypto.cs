using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OPTIC.Services;

/// <summary>
/// Web Push message encryption (RFC 8291, "aes128gcm" content coding from RFC 8188) and VAPID sign-in tokens (RFC 8292),
/// with .NET's own cryptography: P-256 ECDH, HKDF-SHA256, AES-128-GCM and ES256 signatures.
/// </summary>
public static class WebPushCrypto
{
    public static string B64Url(ReadOnlySpan<byte> b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromB64Url(string s)
    {
        s = s.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    static byte[] Uncompressed(ECParameters p) => [0x04, .. p.Q.X!, .. p.Q.Y!];

    static ECParameters PublicFromUncompressed(byte[] point)
    {
        if (point.Length != 65 || point[0] != 0x04) throw new ArgumentException("The browser's key is not an uncompressed P-256 point.");
        return new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..65] } };
    }

    /// <summary>
    /// Encrypts one message for a browser subscription. <paramref name="senderPrivate"/> and <paramref name="salt"/> are only
    /// given by tests (fixed values); normally a new key pair and salt are made for every message.
    /// </summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret, byte[]? senderPrivate = null, byte[]? salt = null)
    {
        using var ua = ECDiffieHellman.Create(PublicFromUncompressed(uaPublic));
        using var sender = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        if (senderPrivate is not null)
        {
            // Import the fixed private key; its public point is derived by .NET from D.
            using var tmp = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = senderPrivate });
            sender.ImportParameters(tmp.ExportParameters(true));
        }
        var asPublic = Uncompressed(sender.ExportParameters(false));
        var shared = sender.DeriveRawSecretAgreement(ua.PublicKey);
        salt ??= RandomNumberGenerator.GetBytes(16);

        // RFC 8291 section 3.4: the input keying material from the shared secret and the browser's auth secret.
        var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, shared, authSecret);
        byte[] keyInfo = [.. "WebPush: info\0"u8, .. uaPublic, .. asPublic];
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);
        // RFC 8188: content encryption key and nonce.
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());

        // One record: the message, then the last-record delimiter 0x02.
        byte[] padded = [.. plaintext, 0x02];
        var cipher = new byte[padded.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16)) aes.Encrypt(nonce, padded, cipher, tag);

        // Header: salt (16) | record size (4, big-endian) | key id length (1) | key id = sender public key (65).
        int recordSize = 4096;
        byte[] header = [.. salt, (byte)(recordSize >> 24), (byte)(recordSize >> 16), (byte)(recordSize >> 8), (byte)recordSize, (byte)asPublic.Length, .. asPublic];
        return [.. header, .. cipher, .. tag];
    }

    /// <summary>The reverse of <see cref="Encrypt"/>, as a browser does it (used by the tests).</summary>
    public static byte[] Decrypt(byte[] body, byte[] uaPrivate, byte[] uaPublic, byte[] authSecret)
    {
        var salt = body[..16];
        int idLen = body[20];
        var asPublic = body[21..(21 + idLen)];
        var rest = body[(21 + idLen)..];
        using var tmp = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = uaPrivate });
        using var ua = ECDiffieHellman.Create(tmp.ExportParameters(true));
        using var sender = ECDiffieHellman.Create(PublicFromUncompressed(asPublic));
        var shared = ua.DeriveRawSecretAgreement(sender.PublicKey);
        var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, shared, authSecret);
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, [.. "WebPush: info\0"u8, .. uaPublic, .. asPublic]);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());
        var plain = new byte[rest.Length - 16];
        using (var aes = new AesGcm(cek, 16)) aes.Decrypt(nonce, rest[..^16], rest[^16..], plain);
        var end = Array.LastIndexOf(plain, (byte)0x02);
        return plain[..end];
    }

    /// <summary>A new VAPID key pair: (private key D, public key as an uncompressed point), both base64url.</summary>
    public static (string PrivateKey, string PublicKey) NewVapidKeys()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = k.ExportParameters(true);
        return (B64Url(p.D!), B64Url(Uncompressed(p)));
    }

    /// <summary>The Authorization header value for a push service: "vapid t=&lt;JWT&gt;, k=&lt;public key&gt;".</summary>
    public static string VapidAuthorization(string endpoint, string subject, string privateKey, string publicKey, DateTime? now = null)
    {
        var uri = new Uri(endpoint);
        var audience = uri.GetLeftPart(UriPartial.Authority);
        var exp = new DateTimeOffset(now ?? DateTime.UtcNow).AddHours(12).ToUnixTimeSeconds();
        var header = B64Url(JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "ES256" }));
        var claims = B64Url(JsonSerializer.SerializeToUtf8Bytes(new { aud = audience, exp, sub = subject }));
        var pub = FromB64Url(publicKey);
        using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = FromB64Url(privateKey), Q = PublicFromUncompressed(pub).Q });
        var signature = key.SignData(Encoding.ASCII.GetBytes(header + "." + claims), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"vapid t={header}.{claims}.{B64Url(signature)}, k={publicKey}";
    }
}
