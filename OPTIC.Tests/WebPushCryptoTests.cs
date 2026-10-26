using System.Security.Cryptography;
using System.Text;
using OPTIC.Services;

namespace OPTIC.Tests;

// The worked example in RFC 8291 section 5 (Message Encryption for Web Push).
public class WebPushCryptoTests
{
    const string Plain = "When I grow up, I want to be a watermelon";
    const string AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    const string UaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    const string Auth = "BTBZMqHH6r4Tts7J_aSIgg";
    const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    const string Expected = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

    [Fact]
    public void Encrypt_matches_the_RFC_8291_example()
    {
        var body = WebPushCrypto.Encrypt(Encoding.UTF8.GetBytes(Plain), WebPushCrypto.FromB64Url(UaPublic), WebPushCrypto.FromB64Url(Auth),
            WebPushCrypto.FromB64Url(AsPrivate), WebPushCrypto.FromB64Url(Salt));
        Assert.Equal(Expected, WebPushCrypto.B64Url(body));
    }

    [Fact]
    public void A_browser_can_decrypt_a_fresh_message()
    {
        var body = WebPushCrypto.Encrypt(Encoding.UTF8.GetBytes("{\"title\":\"Sell 151,000 OPT today\"}"), WebPushCrypto.FromB64Url(UaPublic), WebPushCrypto.FromB64Url(Auth));
        var plain = WebPushCrypto.Decrypt(body, WebPushCrypto.FromB64Url(UaPrivate), WebPushCrypto.FromB64Url(UaPublic), WebPushCrypto.FromB64Url(Auth));
        Assert.Equal("{\"title\":\"Sell 151,000 OPT today\"}", Encoding.UTF8.GetString(plain));
    }

    [Fact]
    public void Vapid_token_is_signed_by_the_key()
    {
        var (priv, pub) = WebPushCrypto.NewVapidKeys();
        var auth = WebPushCrypto.VapidAuthorization("https://fcm.googleapis.com/fcm/send/abc", "mailto:test@example.com", priv, pub);
        Assert.StartsWith("vapid t=", auth);
        var jwt = auth[8..auth.IndexOf(',')];
        var parts = jwt.Split('.');
        Assert.Contains("\"aud\":\"https://fcm.googleapis.com\"", Encoding.UTF8.GetString(WebPushCrypto.FromB64Url(parts[1])));
        var q = WebPushCrypto.FromB64Url(pub);
        using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = q[1..33], Y = q[33..65] } });
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), WebPushCrypto.FromB64Url(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
}
