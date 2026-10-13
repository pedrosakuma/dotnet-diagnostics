using System.Text.Json;
using DotnetDiagnostics.Cli;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.Threads;
using FluentAssertions;

namespace DotnetDiagnostics.Cli.Tests;

public sealed class CliStreamingAddressProjectionTests
{
    private const ulong Address = 0xFEDCBA9876543210;
    private const string AddressText = "0xfedcba9876543210";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void HeapQueryProjection_SerializesAddressesAsExactHexStrings()
    {
        var path = new RetentionPath(
            "Sample.Cache",
            Address,
            [new RetentionFrame("Sample.Root", Address) { RootKind = "Strong" }],
            Truncated: false);
        var result = new HeapSnapshotQueryResult(
            "heap-handle",
            "retention-paths",
            "dump",
            42,
            DateTimeOffset.UnixEpoch)
        {
            RetentionPaths = [path],
            StaticFields =
            [
                new StaticFieldStat(
                    "Sample.Cache",
                    "Sample.dll",
                    "Current",
                    1,
                    Address,
                    "Sample.Entry",
                    64,
                    0),
            ],
            AsyncOperations =
            [
                new AsyncOperationStat("Sample.Operation", 1, "Sample.Awaiter", 32)
                {
                    StateMachineAddress = Address,
                    TaskAddress = Address,
                    Stack = [new AsyncChainFrame("Sample.Operation", 1, "Sample.Awaiter", Address) { TaskAddress = Address }],
                },
            ],
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(CliStreamingProtocol.TrimHeapQueryResult(result), JsonOptions));
        var root = document.RootElement;
        root.GetProperty("retentionPaths")[0].GetProperty("targetObjectAddress").GetString().Should().Be(AddressText);
        root.GetProperty("retentionPaths")[0].GetProperty("chain")[0].GetProperty("objectAddress").GetString().Should().Be(AddressText);
        root.GetProperty("staticFields")[0].GetProperty("valueAddress").GetString().Should().Be(AddressText);
        root.GetProperty("asyncOperations")[0].GetProperty("stateMachineAddress").GetString().Should().Be(AddressText);
        root.GetProperty("asyncOperations")[0].GetProperty("taskAddress").GetString().Should().Be(AddressText);
        root.GetProperty("asyncOperations")[0].GetProperty("stack")[0].GetProperty("stateMachineAddress").GetString().Should().Be(AddressText);
        root.GetProperty("asyncOperations")[0].GetProperty("stack")[0].GetProperty("taskAddress").GetString().Should().Be(AddressText);
    }

    [Fact]
    public void ThreadQueryProjection_SerializesLockAndQueueAddressesAsExactHexStrings()
    {
        var threadPool = new ThreadPoolSnapshot(
            Initialized: true,
            UsingPortableThreadPool: true,
            UsingWindowsThreadPool: false,
            new ThreadPoolWorkerState(1, 1, 0, 0, 1, 8),
            new ThreadPoolIocpState(1, 1, 1, 8),
            new ThreadPoolQueueState(
                1,
                [new ThreadPoolNamedQueueLength("global", 1) { QueueIndex = 0, QueueAddress = Address }],
                [new ThreadPoolLocalQueueLength(Address, 1) { ManagedThreadId = 7 }]),
            PendingWorkItems: 1);
        var result = new ThreadSnapshotQueryResult(
            "thread-handle",
            "deadlocks",
            "dump",
            42,
            DateTimeOffset.UnixEpoch,
            TimeSpan.Zero)
        {
            Deadlocks =
            [
                new ThreadDeadlockCycle(
                    [],
                    [new ThreadDeadlockLink(7, 8, Address, "System.Object", "Monitor")],
                    []),
            ],
            ThreadPool = threadPool,
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(CliStreamingProtocol.TrimThreadQueryResult(result), JsonOptions));
        var root = document.RootElement;
        root.GetProperty("deadlocks")[0].GetProperty("lockChain")[0].GetProperty("lockObjectAddress").GetString().Should().Be(AddressText);
        root.GetProperty("threadPool").GetProperty("queues").GetProperty("globalQueues")[0].GetProperty("queueAddress").GetString().Should().Be(AddressText);
        root.GetProperty("threadPool").GetProperty("queues").GetProperty("localQueues")[0].GetProperty("queueAddress").GetString().Should().Be(AddressText);
    }
}
