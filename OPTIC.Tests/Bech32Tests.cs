using System.Security.Cryptography;
using System.Text;

namespace OPTIC.Tests;

public class Bech32Tests
{
    [Fact]
    public void ModuleAccountMatchesChain()
    {
        // bonded_tokens_pool module account on Optio: bech32("optio", sha256(name)[:20]).
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("bonded_tokens_pool")).AsSpan(0, 20).ToArray();
        Assert.Equal("optio1fl48vsnmsdzcv85q5d2q4z5ajdha8yu3qxeqje", WebDashboard.Bech32Encode("optio", hash));
    }

    [Fact]
    public void ReprefixRoundTrips()
    {
        const string valoper = "optiovaloper1qevr8p59w933ym59tu67p2hmn3ggtv5vlm0zef";
        var account = WebDashboard.Bech32Reprefix(valoper, "optio");
        Assert.NotNull(account);
        Assert.StartsWith("optio1", account);
        Assert.Equal(valoper, WebDashboard.Bech32Reprefix(account!, "optiovaloper"));
    }

    [Fact]
    public void RejectsBadChecksum()
    {
        Assert.Null(WebDashboard.Bech32Decode("optio1fl48vsnmsdzcv85q5d2q4z5ajdha8yu3qxeqjf"));
    }
}
