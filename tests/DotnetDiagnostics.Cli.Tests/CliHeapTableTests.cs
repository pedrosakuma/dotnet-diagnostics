using System.Globalization;
using System.Text;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Dump;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

/// <summary>
/// Coverage for the <c>inspect-heap</c> human table identity columns (issue #301 #3). The
/// <c>(ModuleVersionId, MetadataToken)</c> pair is what <c>get-bytes --kind module --mvid &lt;guid&gt;</c>
/// needs, but it previously lived only in <c>--json</c>. The human table now exposes a short ID column
/// and an identities block so an operator can copy the GUID without dropping to JSON.
/// </summary>
public sealed class CliHeapTableTests
{
    [Fact]
    public void RenderTopTypes_WithIdentity_EmitsIdColumnAndIdentitiesBlock()
    {
        var mvid = Guid.Parse("11112222-3333-4444-5555-666677778888");
        var rows = new List<TypeStat>
        {
            new("My.App.Widget", "My.App.dll", 1234, 9_000, 41.2,
                new TypeIdentity("My.App.Widget") { ModuleVersionId = mvid, MetadataToken = 0x02000042 }),
            new("System.String", "System.Private.CoreLib.dll", 50_000, 4_000, 18.7, Identity: null),
        };
        var sb = new StringBuilder();

        CliCommands.RenderTopTypes(sb, rows);
        var rendered = sb.ToString();

        rendered.Should().Contain("ID");
        // The type carrying an identity gets handle 1 + an identities line with the copyable mvid/token.
        rendered.Should().Contain("identities (for get-bytes --kind module --mvid <guid>):");
        rendered.Should().Contain("1: mvid=11112222-3333-4444-5555-666677778888 token=0x02000042");
        // The string row has no identity, so it never gets a handle.
        rendered.Should().NotContain("2: mvid=");
    }

    [Fact]
    public void RenderTopTypes_WithoutAnyIdentity_OmitsIdentitiesBlock()
    {
        var rows = new List<TypeStat>
        {
            new("System.Byte[]", null, 10, 2_000, 5.0, Identity: null),
        };
        var sb = new StringBuilder();

        CliCommands.RenderTopTypes(sb, rows);
        var rendered = sb.ToString();

        rendered.Should().NotContain("identities (");
    }

    [Fact]
    public void RenderTopTypes_FormatsTokenInvariantHex()
    {
        var rows = new List<TypeStat>
        {
            new("T", "m.dll", 1, 100, 10.0, new TypeIdentity("T") { ModuleVersionId = Guid.Empty, MetadataToken = 1 }),
        };
        var sb = new StringBuilder();

        CliCommands.RenderTopTypes(sb, rows);

        sb.ToString().Should().Contain("token=0x00000001", "metadata tokens render as zero-padded invariant hex");
    }

    [Fact]
    public void RenderTopTypes_Empty_WritesNothing()
    {
        var sb = new StringBuilder();

        CliCommands.RenderTopTypes(sb, Array.Empty<TypeStat>());

        sb.ToString().Should().BeEmpty();
    }
}
