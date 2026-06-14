using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnostics.Core.CpuSampling;

/// <summary>
/// The authoritative set of managed method symbols emitted by the NativeAOT ILC compiler into
/// its <c>*.map.xml</c> map file (published with <c>&lt;IlcGenerateMapFile&gt;true&lt;/IlcGenerateMapFile&gt;</c>,
/// i.e. <c>ilc --map:…</c>). Used by <see cref="PerfNativeAotCpuSampler"/> as a membership gate so a
/// <see cref="DotnetDiagnostics.Core.Memory.MethodIdentity"/> is emitted only for genuine managed
/// <c>MethodCode</c> frames and not for runtime helpers, P/Invoke shims, libc, or kernel symbols
/// (issue #395 — unblock the <c>dotnet-native-mcp</c> "disassemble this hot AOT function" handoff).
/// </summary>
/// <remarks>
/// <para>
/// The map file is produced by ILC's <c>XmlObjectDumper</c>. Each emitted object node becomes an
/// element whose tag is the node kind (<c>MethodCode</c>, <c>UnboxingStub</c>, <c>ReadyToRunHelper</c>,
/// …) and whose <c>Name</c> attribute is the <em>mangled</em> symbol name — byte-for-byte identical
/// to the symbol <c>perf script</c> reports for a frame (verified against an unstripped Linux
/// NativeAOT publish). The map carries <c>Length</c> and a content <c>Hash</c> but, importantly,
/// <strong>no addresses</strong>, so resolution is by symbol name, not by an address-range lookup.
/// </para>
/// <para>
/// Only <c>MethodCode</c> nodes are indexed: those are the real managed method bodies a consumer
/// can disassemble. Stub / helper nodes are intentionally excluded to keep the handoff high-signal.
/// </para>
/// </remarks>
public sealed class NativeAotMethodMap
{
    /// <summary>The map-file element tag that wraps a real managed method body.</summary>
    private const string MethodCodeElement = "MethodCode";

    private readonly HashSet<string> _managedMethodSymbols;

    private NativeAotMethodMap(HashSet<string> managedMethodSymbols)
    {
        _managedMethodSymbols = managedMethodSymbols;
    }

    /// <summary>Number of distinct managed <c>MethodCode</c> symbols indexed from the map file.</summary>
    public int Count => _managedMethodSymbols.Count;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="mangledSymbol"/> is the mangled name of a managed
    /// <c>MethodCode</c> node — i.e. the frame is a genuine AOT-compiled managed method body. The
    /// lookup is ordinal and case-sensitive (mangled names are case-significant).
    /// </summary>
    public bool ContainsMethod(string? mangledSymbol)
        => !string.IsNullOrEmpty(mangledSymbol) && _managedMethodSymbols.Contains(mangledSymbol);

    /// <summary>
    /// Streams the ILC <c>*.map.xml</c> at <paramref name="mapFilePath"/> and indexes every
    /// <c>MethodCode</c> node's mangled <c>Name</c>. The file can be tens of MB for a real app, so it
    /// is read with a forward-only <see cref="XmlReader"/> (no DOM materialisation). Returns
    /// <c>null</c> when the path is missing, unreadable, or malformed — callers degrade gracefully to
    /// the existing ELF-demangle-only behaviour rather than failing the whole sample.
    /// </summary>
    public static NativeAotMethodMap? TryLoad(string? mapFilePath, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;

        if (string.IsNullOrWhiteSpace(mapFilePath))
        {
            return null;
        }

        if (!File.Exists(mapFilePath))
        {
            logger.LogWarning("NativeAOT map file '{MapFile}' does not exist; AOT MethodIdentity resolution is disabled for this sample.", mapFilePath);
            return null;
        }

        try
        {
            using var stream = new FileStream(mapFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16, FileOptions.SequentialScan);
            return Load(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            logger.LogWarning(ex, "Failed to read NativeAOT map file '{MapFile}'; AOT MethodIdentity resolution is disabled for this sample.", mapFilePath);
            return null;
        }
    }

    /// <summary>
    /// Parses a map-file stream. Exposed for unit testing against in-memory fixtures; production
    /// callers use <see cref="TryLoad"/>, which adds path/IO guards.
    /// </summary>
    public static NativeAotMethodMap Load(Stream mapXml)
    {
        ArgumentNullException.ThrowIfNull(mapXml);

        var symbols = new HashSet<string>(StringComparer.Ordinal);
        var settings = new XmlReaderSettings
        {
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
            DtdProcessing = DtdProcessing.Prohibit,
            CloseInput = false,
        };

        using var reader = XmlReader.Create(mapXml, settings);
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element ||
                !string.Equals(reader.LocalName, MethodCodeElement, StringComparison.Ordinal))
            {
                continue;
            }

            var name = reader.GetAttribute("Name");
            if (!string.IsNullOrEmpty(name))
            {
                symbols.Add(name);
            }
        }

        return new NativeAotMethodMap(symbols);
    }
}
