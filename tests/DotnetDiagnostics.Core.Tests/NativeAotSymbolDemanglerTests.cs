using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

/// <summary>
/// Tests for <see cref="NativeAotSymbolDemangler"/> (issue #29). Mangled inputs were
/// captured from a real unstripped NativeAOT Linux x64 publish of <c>samples/NativeAotSample</c>
/// (<c>nm --defined-only</c>); the demangler is best-effort and the assertions only check
/// the human-actionable shape of the output, not character-perfect parity with managed names.
/// </summary>
public class NativeAotSymbolDemanglerTests
{
    [Fact]
    public void Demangle_NullOrEmpty_ReturnsInput()
    {
        NativeAotSymbolDemangler.Demangle(null).Should().BeEmpty();
        NativeAotSymbolDemangler.Demangle("").Should().BeEmpty();
    }

    [Fact]
    public void Demangle_NativeSymbol_PassThrough()
    {
        NativeAotSymbolDemangler.Demangle("__libc_start_main").Should().Be("__libc_start_main");
        NativeAotSymbolDemangler.Demangle("CryptoNative_BioRead").Should().Be("CryptoNative_BioRead");
        NativeAotSymbolDemangler.Demangle("realloc").Should().Be("realloc");
        NativeAotSymbolDemangler.Demangle("[unknown]").Should().Be("[unknown]");
    }

    [Fact]
    public void Demangle_SystemPrivateCoreLib_PrefixIsExpanded()
    {
        var result = NativeAotSymbolDemangler.Demangle("S_P_CoreLib_System_String__Equals");
        result.Should().StartWith("System.Private.CoreLib.");
        result.Should().Contain("System.String");
        result.Should().EndWith(".Equals");
    }

    [Fact]
    public void Demangle_AssemblyNamespaceTypeMethod_BoundaryRestored()
    {
        var result = NativeAotSymbolDemangler.Demangle(
            "Microsoft_AspNetCore_Http_Microsoft_AspNetCore_Http_HeaderDictionary__get_ContentLength");

        // Type/method boundary preserved.
        result.Should().EndWith(".get_ContentLength");
        result.Should().Contain("HeaderDictionary");
        result.Should().NotContain("__");
    }

    [Fact]
    public void Demangle_GenericArity_RenderedAsBacktick()
    {
        // Generic arity (`_2`) sits inside the TYPE-FQN segment (before the first "__"
        // method boundary), reflecting how ilc names generic types: "Dictionary_2".
        var result = NativeAotSymbolDemangler.Demangle(
            "System_Collections_Generic_Dictionary_2_Enumerator__MoveNext");

        result.Should().Contain("Dictionary`2");
        result.Should().EndWith(".MoveNext");
    }

    [Fact]
    public void Demangle_GenericArguments_AreCommaSeparated()
    {
        var result = NativeAotSymbolDemangler.Demangle(
            "Foo<Bar__Baz>__Method");

        // <Bar, Baz> shape preserved at the type-arg level.
        result.Should().Contain("<");
        result.Should().Contain(", ");
        result.Should().EndWith(".Method");
    }

    [Fact]
    public void Demangle_BoxedUnboxStub_TaggedAndCollapsed()
    {
        // Real symbol captured from the NativeAotSample binary; the duplicated body is
        // expected to be dropped and the suffix annotated.
        const string mangled = "<Boxed>Some_Type__<unbox>Some_Type__Method";
        var result = NativeAotSymbolDemangler.Demangle(mangled);

        result.Should().EndWith(" (boxed)");
        result.Should().Contain("Method");
        // No leftover marker characters.
        result.Should().NotContain("<Boxed>");
        result.Should().NotContain("<unbox>");
    }

    [Fact]
    public void Demangle_UnboxStub_Tagged()
    {
        var result = NativeAotSymbolDemangler.Demangle("unbox_UIntPtr__TryFormat");

        result.Should().EndWith(" (unbox)");
        result.Should().Contain("UIntPtr");
        result.Should().Contain("TryFormat");
    }

    [Fact]
    public void Demangle_RealNativeAotSymbol_BecomesReadable()
    {
        // Real symbol from the NativeAotSample publish; the unstripped ELF shipped this
        // 200-char string. Demangling is a quality-of-life win: any improvement helps.
        const string mangled =
            "Microsoft_AspNetCore_Http_Microsoft_AspNetCore_Internal_AdaptiveCapacityDictionary_2_Enumerator__MoveNext";

        var result = NativeAotSymbolDemangler.Demangle(mangled);

        result.Length.Should().BeLessThan(mangled.Length);
        result.Should().EndWith("MoveNext");
        result.Should().Contain("Microsoft.AspNetCore");
    }

    [Theory]
    [InlineData(null, NativeAotSymbolDemangler.SymbolSource.Stripped)]
    [InlineData("", NativeAotSymbolDemangler.SymbolSource.Stripped)]
    [InlineData("[unknown]", NativeAotSymbolDemangler.SymbolSource.Stripped)]
    [InlineData("0x7fa1234abcde", NativeAotSymbolDemangler.SymbolSource.Stripped)]
    [InlineData("__libc_start_main", NativeAotSymbolDemangler.SymbolSource.Native)]
    [InlineData("CryptoNative_BioRead", NativeAotSymbolDemangler.SymbolSource.Native)]
    [InlineData("pthread_mutex_lock", NativeAotSymbolDemangler.SymbolSource.Native)]
    [InlineData("clock_gettime", NativeAotSymbolDemangler.SymbolSource.Native)]
    [InlineData("epoll_wait", NativeAotSymbolDemangler.SymbolSource.Native)]
    [InlineData("S_P_CoreLib_System_String__Equals", NativeAotSymbolDemangler.SymbolSource.ElfMangled)]
    [InlineData("Microsoft_AspNetCore_Http_T__M", NativeAotSymbolDemangler.SymbolSource.ElfMangled)]
    [InlineData("MyType.MyMethod", NativeAotSymbolDemangler.SymbolSource.ElfDemangled)]
    public void Classify_ReturnsExpectedSource(string? symbol, NativeAotSymbolDemangler.SymbolSource expected)
    {
        NativeAotSymbolDemangler.Classify(symbol).Should().Be(expected);
    }

    [Theory]
    [InlineData("pthread_mutex_lock")]
    [InlineData("clock_gettime")]
    [InlineData("epoll_wait")]
    [InlineData("malloc_trim")]
    [InlineData("curl_easy_perform")]
    public void Demangle_GenericNativeFunctionsWithUnderscores_PassThrough(string nativeSymbol)
    {
        // Review fix #1: `_` in C symbol names must not be converted to '.' just because the
        // hardcoded native prefix allowlist doesn't list every libc / PInvoke function.
        NativeAotSymbolDemangler.Demangle(nativeSymbol).Should().Be(nativeSymbol);
    }

    [Theory]
    [InlineData("MyNamespace.Type.get_Value")]
    [InlineData("HeaderDictionary.get_ContentLength")]
    [InlineData("System.String.Equals")]
    public void Demangle_AlreadyDemangledNames_AreReturnedUnchanged(string display)
    {
        // Review fix #2: Demangle must be idempotent on its own output. Previously names
        // containing `_` (e.g. property accessor get_X) had the underscore rewritten to '.'.
        NativeAotSymbolDemangler.Demangle(display).Should().Be(display);
        NativeAotSymbolDemangler.Demangle(NativeAotSymbolDemangler.Demangle(display)).Should().Be(display);
    }

    [Fact]
    public void Demangle_IsIdempotent_OnMangledInputAfterFirstPass()
    {
        const string mangled = "S_P_CoreLib_System_String__Equals";
        var first = NativeAotSymbolDemangler.Demangle(mangled);
        var second = NativeAotSymbolDemangler.Demangle(first);
        second.Should().Be(first);
    }

    [Fact]
    public void Demangle_BoxedUnboxStub_MismatchedHalves_PreservesBoth()
    {
        // Review fix #4: when the boxed and unbox halves differ (explicit interface impl
        // pattern), neither half can be silently discarded.
        var result = NativeAotSymbolDemangler.Demangle(
            "<Boxed>IFoo__<unbox>ConcreteFoo__M");

        result.Should().EndWith(" (boxed)");
        result.Should().Contain("IFoo");
        result.Should().Contain("ConcreteFoo");
        result.Should().Contain("M");
    }

    [Fact]
    public void Combine_DistinctConcreteSources_ReturnsMixed()
    {
        // Review fix #3: a trace mixing stripped/native/managed frames must not silently
        // present as fully symbolized.
        NativeAotSymbolDemangler.Combine(
            NativeAotSymbolDemangler.SymbolSource.ElfDemangled,
            NativeAotSymbolDemangler.SymbolSource.Stripped)
            .Should().Be(NativeAotSymbolDemangler.SymbolSource.Mixed);

        NativeAotSymbolDemangler.Combine(
            NativeAotSymbolDemangler.SymbolSource.Native,
            NativeAotSymbolDemangler.SymbolSource.Stripped)
            .Should().Be(NativeAotSymbolDemangler.SymbolSource.Mixed);
    }

    [Fact]
    public void Combine_MangledAndDemangled_CollapsesToDemangled()
    {
        // Both indicate managed names; the artifact ships demangled strings, so the rollup
        // should reflect that quality level rather than reporting Mixed.
        NativeAotSymbolDemangler.Combine(
            NativeAotSymbolDemangler.SymbolSource.ElfMangled,
            NativeAotSymbolDemangler.SymbolSource.ElfDemangled)
            .Should().Be(NativeAotSymbolDemangler.SymbolSource.ElfDemangled);
    }

    [Theory]
    // Demangled display => (TypeFullName, MethodName). Splits on the last top-level '.'.
    [InlineData("NativeAotSample.WeatherForecast.ToString", "NativeAotSample.WeatherForecast", "ToString")]
    [InlineData("System.Net.Primitives.System.Net.Cookie.SetDomainAndKey", "System.Net.Primitives.System.Net.Cookie", "SetDomainAndKey")]
    [InlineData("NativeAotSample.WeatherForecast.get_TemperatureF", "NativeAotSample.WeatherForecast", "get_TemperatureF")]
    public void SplitTypeAndMethod_SplitsOnLastTopLevelDot(string display, string expectedType, string expectedMethod)
    {
        var (type, method) = NativeAotSymbolDemangler.SplitTypeAndMethod(display);
        type.Should().Be(expectedType);
        method.Should().Be(expectedMethod);
    }

    [Fact]
    public void SplitTypeAndMethod_IgnoresDotsInsideGenericArguments()
    {
        var (type, method) = NativeAotSymbolDemangler.SplitTypeAndMethod(
            "MyApp.Handler`1<System.Collections.Generic.List`1<System.Int32>>.Handle");
        type.Should().Be("MyApp.Handler`1<System.Collections.Generic.List`1<System.Int32>>");
        method.Should().Be("Handle");
    }

    [Fact]
    public void SplitTypeAndMethod_StripsDemanglerDecorations()
    {
        // The demangler appends human-only suffixes; they must not leak into the method name.
        NativeAotSymbolDemangler.SplitTypeAndMethod("My.Type.Method [canon]").Should().Be(("My.Type", "Method"));
        NativeAotSymbolDemangler.SplitTypeAndMethod("My.Type.Method (boxed)").Should().Be(("My.Type", "Method"));
        NativeAotSymbolDemangler.SplitTypeAndMethod("My.Type.Method (unbox)").Should().Be(("My.Type", "Method"));
    }

    [Fact]
    public void SplitTypeAndMethod_NoBoundary_ReturnsNullType()
    {
        var (type, method) = NativeAotSymbolDemangler.SplitTypeAndMethod("__libc_start_main");
        type.Should().BeNull();
        method.Should().Be("__libc_start_main");
    }
}
