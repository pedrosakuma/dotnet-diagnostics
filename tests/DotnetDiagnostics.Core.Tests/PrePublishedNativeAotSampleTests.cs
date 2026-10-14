using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class PrePublishedNativeAotSampleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory, "test-artifacts", nameof(PrePublishedNativeAotSampleTests), Guid.NewGuid().ToString("N"));

    public PrePublishedNativeAotSampleTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCopyTo_ReturnsFalse_WhenNothingIsConfigured(string? configured)
        => PrePublishedNativeAotSample.TryCopyTo(configured, Path.Combine(_root, "dest")).Should().BeFalse();

    [Theory]
    [InlineData("NativeAotSample.exe")]
    [InlineData("NativeAotSample.pdb")]
    public void TryCopyTo_Throws_WhenConfiguredDirectoryLacksRequiredFile(string missing)
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        foreach (var name in new[] { "NativeAotSample.exe", "NativeAotSample.pdb" }.Where(name => name != missing))
        {
            File.WriteAllText(Path.Combine(source, name), "x");
        }

        var act = () => PrePublishedNativeAotSample.TryCopyTo(source, Path.Combine(_root, "dest"));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{missing}*");
        Directory.Exists(Path.Combine(_root, "dest")).Should().BeFalse();
    }

    [Fact]
    public void TryCopyTo_Throws_WhenConfiguredDirectoryDoesNotExist()
    {
        var act = () => PrePublishedNativeAotSample.TryCopyTo(Path.Combine(_root, "absent"), Path.Combine(_root, "dest"));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TryCopyTo_CopiesFilesWithoutModifyingTheSource()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "NativeAotSample.exe"), "exe");
        File.WriteAllText(Path.Combine(source, "NativeAotSample.pdb"), "pdb");
        File.WriteAllText(Path.Combine(source, "nested", "extra.bin"), "extra");
        var destination = Path.Combine(_root, "dest");

        PrePublishedNativeAotSample.TryCopyTo(source, destination).Should().BeTrue();

        File.ReadAllText(Path.Combine(destination, "NativeAotSample.exe")).Should().Be("exe");
        File.ReadAllText(Path.Combine(destination, "NativeAotSample.pdb")).Should().Be("pdb");
        File.ReadAllText(Path.Combine(destination, "nested", "extra.bin")).Should().Be("extra");
        File.WriteAllText(Path.Combine(destination, "NativeAotSample.pdb"), "modified");
        File.ReadAllText(Path.Combine(source, "NativeAotSample.pdb")).Should().Be("pdb");
    }
}
