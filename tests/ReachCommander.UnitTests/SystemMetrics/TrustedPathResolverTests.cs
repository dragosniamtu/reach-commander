using ReachCommander.Infrastructure.SystemMetrics;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class TrustedPathResolverTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    [Fact]
    public void Is_within_root_accepts_descendants_and_rejects_sibling_prefixes()
    {
        var root = _temporary.CreateDirectory("sys");
        var inside = _temporary.CreateDirectory(Path.Combine("sys", "devices", "cpu"));
        var sibling = _temporary.CreateDirectory("sys-escape");
        var resolver = new TrustedPathResolver(StubHostPlatform.Windows);

        Assert.True(resolver.IsWithinRoot(root, inside));
        Assert.False(resolver.IsWithinRoot(root, sibling));
    }

    [Fact]
    public void Canonical_containment_rejects_parent_and_rooted_relative_results()
    {
        var root = Path.GetFullPath(Path.Combine(_temporary.Path, "sys"));
        var outside = Path.GetFullPath(Path.Combine(_temporary.Path, "elsewhere"));

        Assert.False(TrustedPathResolver.IsCanonicalPathWithinRoot(
            root,
            outside,
            StringComparison.OrdinalIgnoreCase));
        Assert.True(TrustedPathResolver.IsCanonicalPathWithinRoot(
            root,
            Path.Combine(root, "class", "hwmon"),
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Get_canonical_path_returns_a_full_path_for_regular_entries()
    {
        var directory = _temporary.CreateDirectory(Path.Combine("sys", "class"));
        var resolver = new TrustedPathResolver(StubHostPlatform.Windows);

        var result = resolver.GetCanonicalPath(directory);

        Assert.Equal(Path.GetFullPath(directory), result);
    }

    public void Dispose() => _temporary.Dispose();
}
