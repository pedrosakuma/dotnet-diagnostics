using System.Reflection;
using DotnetDiagnostics.Core.CpuSampling;

namespace DotnetDiagnostics.Core.Tests;

public sealed class CpuSamplerApiCompatibilityTests
{
    // Mirrors an implementer compiled against the pre-#1148 interface: only the original abstract member.
    private sealed class LegacyCpuSampler : ICpuSampler
    {
        public int Calls;
        public CancellationToken LastToken;
        public bool LastExportTrace;

        public Task<CpuSampleResult> SampleAsync(
            int processId,
            TimeSpan duration,
            int topN = 25,
            SourceResolutionOptions? sourceResolution = null,
            MethodInstantiationResolutionOptions? methodInstantiationResolution = null,
            NativeAotSymbolResolutionOptions? nativeAotSymbols = null,
            bool exportTrace = false,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastToken = cancellationToken;
            LastExportTrace = exportTrace;
            return Task.FromResult<CpuSampleResult>(null!);
        }
    }

    private sealed class LegacyRoutingCpuSampler : ICpuSampler
    {
        public CpuSamplingMode? LastMode;

        public Task<CpuSampleResult> SampleAsync(
            int processId, TimeSpan duration, int topN = 25, SourceResolutionOptions? sourceResolution = null,
            MethodInstantiationResolutionOptions? methodInstantiationResolution = null,
            NativeAotSymbolResolutionOptions? nativeAotSymbols = null, bool exportTrace = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult<CpuSampleResult>(null!);

        public Task<CpuSampleResult> SampleAsync(
            int processId, TimeSpan duration, int topN, SourceResolutionOptions? sourceResolution,
            MethodInstantiationResolutionOptions? methodInstantiationResolution,
            NativeAotSymbolResolutionOptions? nativeAotSymbols, bool exportTrace, CpuSamplingMode mode,
            CancellationToken cancellationToken = default)
        {
            LastMode = mode;
            return Task.FromResult<CpuSampleResult>(null!);
        }
    }

    [Fact]
    public async Task LegacyModeOverloadImplementerReceivesModeCallsWithOptions()
    {
        var legacy = new LegacyRoutingCpuSampler();
        ICpuSampler sampler = legacy;

        await sampler.SampleAsync(1, TimeSpan.FromSeconds(1), 5, null, null, null, false, CpuSamplingMode.Os, captureOptions: null, CancellationToken.None);
        Assert.Equal(CpuSamplingMode.Os, legacy.LastMode);
    }

    [Fact]
    public async Task LegacyPositionalCallersStillBind()
    {
        using var cts = new CancellationTokenSource();
        var legacy = new LegacyCpuSampler();
        ICpuSampler sampler = legacy;

        await sampler.SampleAsync(1, TimeSpan.FromSeconds(1), cancellationToken: cts.Token);
        Assert.Equal(cts.Token, legacy.LastToken);

        await sampler.SampleAsync(1, TimeSpan.FromSeconds(1), 5, null, null, null, true, cts.Token);
        Assert.True(legacy.LastExportTrace);
        Assert.Equal(cts.Token, legacy.LastToken);

        await sampler.SampleAsync(1, TimeSpan.FromSeconds(1), 5, null, null, null, false, CpuSamplingMode.Automatic, cts.Token);
        Assert.Equal(cts.Token, legacy.LastToken);
        Assert.Equal(3, legacy.Calls);
    }

    [Fact]
    public async Task LegacyNamedArgumentsStillBind()
    {
        using var cts = new CancellationTokenSource();
        var legacy = new LegacyCpuSampler();
        ICpuSampler sampler = legacy;

        await sampler.SampleAsync(1, TimeSpan.FromSeconds(1), topN: 3, exportTrace: true, cancellationToken: cts.Token);
        Assert.True(legacy.LastExportTrace);
        Assert.Equal(cts.Token, legacy.LastToken);
    }

    [Fact]
    public async Task LegacyImplementerWorksWithOptionsOverloads()
    {
        using var cts = new CancellationTokenSource();
        var legacy = new LegacyCpuSampler();
        ICpuSampler sampler = legacy;

        await sampler.SampleAsync(1, TimeSpan.FromSeconds(1), 5, null, null, null, false, captureOptions: null, cts.Token);
        await sampler.SampleAsync(1, TimeSpan.FromSeconds(1), 5, null, null, null, false, CpuSamplingMode.Automatic, new CpuCaptureOptions(), cts.Token);
        Assert.Equal(2, legacy.Calls);
        Assert.Equal(cts.Token, legacy.LastToken);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => sampler.SampleAsync(
            1, TimeSpan.FromSeconds(1), 5, null, null, null, false, new CpuCaptureOptions(CaptureInlining: true), cts.Token));
        Assert.Equal("captureOptions", ex.ParamName);
        Assert.Equal(2, legacy.Calls);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sampler.SampleAsync(
            1, TimeSpan.FromSeconds(1), 5, null, null, null, false, CpuSamplingMode.Os, captureOptions: null, cts.Token));
    }

    [Theory]
    [InlineData(typeof(ICpuSampler))]
    [InlineData(typeof(EventPipeCpuSampler))]
    [InlineData(typeof(RoutingCpuSampler))]
    [InlineData(typeof(PerfNativeAotCpuSampler))]
    [InlineData(typeof(EtwNativeAotCpuSampler))]
    public void PreviousPublicSignaturesRemainOnType(Type type)
    {
        var legacy = new[]
        {
            typeof(int), typeof(TimeSpan), typeof(int), typeof(SourceResolutionOptions),
            typeof(MethodInstantiationResolutionOptions), typeof(NativeAotSymbolResolutionOptions),
            typeof(bool), typeof(CancellationToken),
        };
        Assert.NotNull(type.GetMethod("SampleAsync", legacy));

        if (type == typeof(ICpuSampler) || type == typeof(RoutingCpuSampler))
        {
            var legacyMode = legacy[..7].Append(typeof(CpuSamplingMode)).Append(typeof(CancellationToken)).ToArray();
            Assert.NotNull(type.GetMethod("SampleAsync", legacyMode));
        }
    }

    [Fact]
    public void CancellationTokenIsLastOnEverySampleAsyncOverload()
    {
        foreach (var type in new[] { typeof(ICpuSampler), typeof(RoutingCpuSampler), typeof(EventPipeCpuSampler) })
        {
            foreach (var m in type.GetMethods().Where(m => m.Name == "SampleAsync"))
            {
                Assert.Equal(typeof(CancellationToken), m.GetParameters()[^1].ParameterType);
            }
        }
    }
}
