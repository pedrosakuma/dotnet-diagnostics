using DotnetDiagnostics.Core.CpuSampling;
using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests;

public sealed class AsyncStateMachineFrameFolderTests
{
    [Theory]
    [InlineData(
        "B3.Umdf.FixConflated.FixTcpClientSession+<WriteLoopAsync>d__22.MoveNext()",
        "B3.Umdf.FixConflated.FixTcpClientSession.WriteLoopAsync() [async]")]
    [InlineData(
        "MyApp.Worker+<RunAsync>d__3.MoveNext()",
        "MyApp.Worker.RunAsync() [async]")]
    [InlineData(
        // Nested owner type (Outer+Inner) must fold its '+' separators too.
        "MyApp.Outer+Inner+<DoWorkAsync>d__7.MoveNext()",
        "MyApp.Outer.Inner.DoWorkAsync() [async]")]
    [InlineData(
        // No enclosing namespace — still folds.
        "Worker+<LoopAsync>d__1.MoveNext()",
        "Worker.LoopAsync() [async]")]
    [InlineData(
        // Real shape captured against a live ASP.NET Core Kestrel process: a non-generic async method.
        "Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal.SocketConnection+<DoSend>d__28.MoveNext()",
        "Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.Internal.SocketConnection.DoSend() [async]")]
    [InlineData(
        // Real shape captured live: a generic async method on a generic type carries an extra
        // arity/instantiation suffix between "d__NN" and ".MoveNext()".
        "System.Text.Json.Serialization.Metadata.JsonTypeInfo`1+<SerializeAsync>d__15[System.__Canon].MoveNext()",
        "System.Text.Json.Serialization.Metadata.JsonTypeInfo.SerializeAsync() [async]")]
    public void TryFold_RecognizedMoveNextShape_FoldsToDeclaringMethod(string raw, string expectedFolded)
    {
        var folded = AsyncStateMachineFrameFolder.TryFold(raw, out var result);

        folded.Should().BeTrue();
        result.Should().Be(expectedFolded);
    }

    [Theory]
    [InlineData("MyApp.Worker.BurnCpu()")]
    [InlineData("System.Threading.LowLevelLifoSemaphore.WaitForSignal(System.Int32,System.Int32)")]
    // Contains "d__" in an unrelated identifier but does not match the MoveNext shape.
    [InlineData("MyApp.Weird.d__Helper.Run()")]
    // MoveNext-shaped but missing the "d__NN" suffix.
    [InlineData("MyApp.Worker+<RunAsync>.MoveNext()")]
    [InlineData(
        // Real shape captured live: an async lambda compiles to a bare "d" state machine (no "__NN"
        // digits). Deliberately unfolded — see the acknowledged-gap remark on the class doc comment.
        "Program+<>c+<<Main>b__0_3>d.MoveNext()")]
    [InlineData(
        // Real shape: an async local function compiles the same bare-"d" way, with "g__Name|N_M" inner
        // naming instead of "b__N_M". Also deliberately left unfolded.
        "Program+<<Main>g__LocalAsync|0_0>d.MoveNext()")]
    [InlineData(
        // Real false-positive risk captured live: AsyncStateMachineBox<T>'s OWN MoveNext embeds the
        // wrapped business method's "+<Method>d__NN[...]" shape as one of its own generic type
        // arguments. Folding this would misattribute the box's frame to the wrapped method.
        "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Threading.Tasks.VoidTaskResult,Microsoft.AspNetCore.Server.Kestrel.Core.Internal.HttpConnection+<ProcessRequestsAsync>d__12`1[System.__Canon]].MoveNext()")]
    public void TryFold_UnrecognizedShape_ReturnsFalseAndEchoesOriginal(string raw)
    {
        var folded = AsyncStateMachineFrameFolder.TryFold(raw, out var result);

        folded.Should().BeFalse();
        result.Should().Be(raw);
    }
}
