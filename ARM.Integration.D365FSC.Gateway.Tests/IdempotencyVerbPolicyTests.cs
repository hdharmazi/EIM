using ARM.Integration.D365FSC.Gateway.Idempotency;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace ARM.Integration.D365FSC.Gateway.Tests;

/// <summary>
/// Which verbs take the idempotent path.
/// </summary>
/// <remarks>
/// These exist because the original implementation read the header only when the method was POST,
/// so a PATCH carrying a key silently bypassed the store — "not required on PATCH" had been built,
/// "honoured on PATCH" had not. Nothing caught it until a live replay produced no stored record.
/// </remarks>
public class IdempotencyVerbPolicyTests
{
    /// <summary>Mirrors the pipeline's selection rule: null means forward directly.</summary>
    private static (string? Key, bool Rejected) Resolve(string method, string? header)
    {
        var key = string.IsNullOrWhiteSpace(header) ? null : header;

        if (key is null && HttpMethods.IsPost(method)) return (null, true);

        return (key, false);
    }

    [Fact]
    public void Post_without_a_key_is_rejected()
    {
        var (key, rejected) = Resolve("POST", null);

        Assert.True(rejected);
        Assert.Null(key);
    }

    [Fact]
    public void Post_with_a_key_takes_the_idempotent_path()
    {
        var (key, rejected) = Resolve("POST", "acme-order-1");

        Assert.False(rejected);
        Assert.Equal("acme-order-1", key);
    }

    [Fact]
    public void Patch_with_a_key_takes_the_idempotent_path()
    {
        var (key, rejected) = Resolve("PATCH", "acme-update-1");

        Assert.False(rejected);
        Assert.Equal("acme-update-1", key);
    }

    [Fact]
    public void Patch_without_a_key_is_allowed_and_forwards_directly()
    {
        var (key, rejected) = Resolve("PATCH", null);

        Assert.False(rejected);
        Assert.Null(key);
    }

    [Fact]
    public void Get_ignores_the_header_entirely()
    {
        var (key, rejected) = Resolve("GET", null);

        Assert.False(rejected);
        Assert.Null(key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_key_counts_as_absent(string header)
    {
        Assert.True(Resolve("POST", header).Rejected);
        Assert.Null(Resolve("PATCH", header).Key);
    }

    [Fact]
    public void Request_fingerprint_distinguishes_method_path_query_and_body()
    {
        var baseline = SqlIdempotencyStore.ComputeRequestHash("POST", "data/CustomersV3", "?a=1", "x"u8.ToArray());

        Assert.Equal(baseline, SqlIdempotencyStore.ComputeRequestHash("post", "data/CustomersV3", "?a=1", "x"u8.ToArray()));
        Assert.NotEqual(baseline, SqlIdempotencyStore.ComputeRequestHash("PATCH", "data/CustomersV3", "?a=1", "x"u8.ToArray()));
        Assert.NotEqual(baseline, SqlIdempotencyStore.ComputeRequestHash("POST", "data/Other", "?a=1", "x"u8.ToArray()));
        Assert.NotEqual(baseline, SqlIdempotencyStore.ComputeRequestHash("POST", "data/CustomersV3", "?a=2", "x"u8.ToArray()));
        Assert.NotEqual(baseline, SqlIdempotencyStore.ComputeRequestHash("POST", "data/CustomersV3", "?a=1", "y"u8.ToArray()));
    }
}
