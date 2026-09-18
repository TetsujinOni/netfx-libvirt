using NetfxLibvirt.Generated.Remote;
using NetfxLibvirt.Rpc;
using NetfxLibvirt.Xdr;

namespace NetfxLibvirt.Tests.Rpc;

/// <summary>Hermetic tests for <see cref="VirNetRpcClient.OpenStreamAsync"/>
/// and <see cref="VirNetRpcStream"/> — the VIR_NET_CONTINUE/STREAM frame
/// handling <c>REMOTE_PROC_DOMAIN_OPEN_GRAPHICS</c> (and any future
/// stream-opening procedure) needs, verified against the exact frame shape
/// confirmed in <c>reference/go-libvirt-src/socket.go</c> (see
/// <see cref="VirNetRpcStream"/>'s own class doc).</summary>
public class VirNetRpcStreamTests
{
    private static byte[] EncodeError(RemoteError error)
    {
        var writer = new XdrWriter();
        error.Encode(writer);
        return writer.ToArray();
    }

    private static readonly RemoteError SampleError = new()
    {
        Code = 42, Domain = 10, Message = "stream error", Level = 2,
        Dom = null, Str1 = null, Str2 = null, Str3 = null, Int1 = 0, Int2 = 0, Net = null,
    };

    [Fact]
    public async Task OpenStreamAsync_ContinueReply_ReturnsStreamInsteadOfThrowing()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Continue, payload: []);
        var client = new VirNetRpcClient(stream);

        await using var rpcStream = await client.OpenStreamAsync(
            (int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        Assert.NotNull(rpcStream);
        var sent = stream.ReadAllSentFrames();
        Assert.Equal(VirNetMessageType.Call, sent[0].Header.Type);
        Assert.Equal((int)RemoteProcedure.RemoteProcDomainOpenGraphics, sent[0].Header.Proc);
    }

    [Fact]
    public async Task OpenStreamAsync_ErrorReply_ThrowsLibvirtRpcException()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Error, EncodeError(SampleError));
        var client = new VirNetRpcClient(stream);

        var ex = await Assert.ThrowsAsync<LibvirtRpcException>(
            () => client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken));

        Assert.Equal(42, ex.Error.Code);
    }

    [Fact]
    public async Task OpenStreamAsync_OkReply_ThrowsBecauseThisProcedureShouldAlwaysStream()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Ok, payload: []);
        var client = new VirNetRpcClient(stream);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OpenStreamAsync_WhileAnotherStreamIsOpen_Throws()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Continue, payload: []);
        var client = new VirNetRpcClient(stream);
        await client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CallAsync_WhileStreamIsOpen_Throws()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Continue, payload: []);
        var client = new VirNetRpcClient(stream);
        await client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CallAsync((int)RemoteProcedure.RemoteProcConnectClose, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposingStream_ReleasesClientForFurtherCalls()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Continue, payload: []);
        var client = new VirNetRpcClient(stream);
        var rpcStream = await client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);
        await rpcStream.DisposeAsync();

        stream.QueueReply(serial: 2, VirNetMessageStatus.Ok, payload: []);
        await client.CallAsync((int)RemoteProcedure.RemoteProcConnectClose, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReadAsync_ConcatenatesDataAcrossMultipleContinueFrames()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Continue, payload: []);
        stream.QueueStream(serial: 1, VirNetMessageStatus.Continue, [1, 2, 3]);
        stream.QueueStream(serial: 1, VirNetMessageStatus.Continue, [4, 5]);
        stream.QueueStream(serial: 1, VirNetMessageStatus.Ok, []);
        var client = new VirNetRpcClient(stream);
        await using var rpcStream = await client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        var buffer = new byte[10];
        var total = 0;
        int read;
        while ((read = await rpcStream.ReadAsync(buffer.AsMemory(total), TestContext.Current.CancellationToken)) > 0)
        {
            total += read;
        }

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, buffer[..total]);
    }

    [Fact]
    public async Task ReadAsync_SmallerCallerBufferThanFramePayload_DrainsFrameAcrossMultipleReads()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Continue, payload: []);
        stream.QueueStream(serial: 1, VirNetMessageStatus.Continue, [1, 2, 3, 4, 5]);
        stream.QueueStream(serial: 1, VirNetMessageStatus.Ok, []);
        var client = new VirNetRpcClient(stream);
        await using var rpcStream = await client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        var chunk1 = new byte[2];
        var read1 = await rpcStream.ReadAsync(chunk1, TestContext.Current.CancellationToken);
        var chunk2 = new byte[2];
        var read2 = await rpcStream.ReadAsync(chunk2, TestContext.Current.CancellationToken);
        var chunk3 = new byte[2];
        var read3 = await rpcStream.ReadAsync(chunk3, TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 1, 2 }, chunk1[..read1]);
        Assert.Equal(new byte[] { 3, 4 }, chunk2[..read2]);
        Assert.Equal(new byte[] { 5 }, chunk3[..read3]);
    }

    [Fact]
    public async Task ReadAsync_EmptyContinuePayload_TreatedAsEndOfStream()
    {
        // The documented libvirtd quirk (see VirNetRpcStream's own doc,
        // confirmed against go-libvirt's processIncomingStream comment):
        // end-of-stream can arrive as Continue+empty-payload, not just Ok.
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Continue, payload: []);
        stream.QueueStream(serial: 1, VirNetMessageStatus.Continue, [9]);
        stream.QueueStream(serial: 1, VirNetMessageStatus.Continue, []); // the quirk
        var client = new VirNetRpcClient(stream);
        await using var rpcStream = await client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        var buffer = new byte[10];
        var first = await rpcStream.ReadAsync(buffer, TestContext.Current.CancellationToken);
        var second = await rpcStream.ReadAsync(buffer, TestContext.Current.CancellationToken);

        Assert.Equal(1, first);
        Assert.Equal(9, buffer[0]);
        Assert.Equal(0, second); // EOF
    }

    [Fact]
    public async Task ReadAsync_ErrorFrame_ThrowsLibvirtRpcExceptionWithDecodedError()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Continue, payload: []);
        stream.QueueStream(serial: 1, VirNetMessageStatus.Error, EncodeError(SampleError));
        var client = new VirNetRpcClient(stream);
        await using var rpcStream = await client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        var buffer = new byte[10];
        var ex = await Assert.ThrowsAsync<LibvirtRpcException>(() => rpcStream.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(42, ex.Error.Code);
    }

    [Fact]
    public async Task ReadAsync_UnsolicitedFrameForDifferentSerial_IsForwardedNotMisreadAsStreamData()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Continue, payload: []);
        stream.QueueMessage(procedure: 999, payload: [7, 7]); // unrelated event, different serial (0)
        stream.QueueStream(serial: 1, VirNetMessageStatus.Continue, [1]);
        stream.QueueStream(serial: 1, VirNetMessageStatus.Ok, []);
        var client = new VirNetRpcClient(stream);

        VirNetMessage? received = null;
        client.UnsolicitedMessageReceived += m => received = m;

        await using var rpcStream = await client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);
        var buffer = new byte[10];
        var read = await rpcStream.ReadAsync(buffer, TestContext.Current.CancellationToken);

        Assert.Equal(1, read);
        Assert.Equal(1, buffer[0]);
        Assert.NotNull(received);
        Assert.Equal(999, received.Value.Header.Proc);
    }

    [Fact]
    public async Task WriteAsync_SendsContinueFrameWithSameProgVersProcSerialAsTheOpeningCall()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Continue, payload: []);
        var client = new VirNetRpcClient(stream);
        await using var rpcStream = await client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        await rpcStream.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);

        var sent = stream.ReadAllSentFrames();
        var streamFrame = sent[1];
        Assert.Equal(VirNetMessageType.Stream, streamFrame.Header.Type);
        Assert.Equal(VirNetMessageStatus.Continue, streamFrame.Header.Status);
        Assert.Equal(sent[0].Header.Serial, streamFrame.Header.Serial);
        Assert.Equal(sent[0].Header.Prog, streamFrame.Header.Prog);
        Assert.Equal(sent[0].Header.Vers, streamFrame.Header.Vers);
        Assert.Equal(sent[0].Header.Proc, streamFrame.Header.Proc);
        Assert.Equal(new byte[] { 1, 2, 3 }, streamFrame.Payload);
    }

    [Fact]
    public async Task WriteAsync_EmptyBuffer_SendsNoFrame()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Continue, payload: []);
        var client = new VirNetRpcClient(stream);
        await using var rpcStream = await client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        await rpcStream.WriteAsync(ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        Assert.Single(stream.ReadAllSentFrames()); // just the opening CALL
    }

    [Fact]
    public async Task CompleteWritingAsync_SendsOkFrameWithEmptyPayload_AndIsIdempotent()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Continue, payload: []);
        var client = new VirNetRpcClient(stream);
        await using var rpcStream = await client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        await rpcStream.CompleteWritingAsync(TestContext.Current.CancellationToken);
        await rpcStream.CompleteWritingAsync(TestContext.Current.CancellationToken); // must not send a second frame

        var sent = stream.ReadAllSentFrames();
        Assert.Equal(2, sent.Count); // CALL + the one completion frame
        Assert.Equal(VirNetMessageType.Stream, sent[1].Header.Type);
        Assert.Equal(VirNetMessageStatus.Ok, sent[1].Header.Status);
        Assert.Empty(sent[1].Payload);
    }

    [Fact]
    public async Task AbortAsync_SendsErrorFrame_AndSuppressesDisposalsGracefulCompletion()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Continue, payload: []);
        var client = new VirNetRpcClient(stream);
        var rpcStream = await client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        await rpcStream.AbortAsync(TestContext.Current.CancellationToken);
        await rpcStream.DisposeAsync(); // must not also send a graceful Ok frame

        var sent = stream.ReadAllSentFrames();
        Assert.Equal(2, sent.Count); // CALL + the one abort frame
        Assert.Equal(VirNetMessageStatus.Error, sent[1].Header.Status);
    }

    [Fact]
    public async Task WriteAsync_AfterCompleteWritingAsync_Throws()
    {
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Continue, payload: []);
        var client = new VirNetRpcClient(stream);
        await using var rpcStream = await client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);
        await rpcStream.CompleteWritingAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rpcStream.WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task DisposeAsync_WithoutExplicitCompleteOrAbort_SendsGracefulOkFrame()
    {
        // Ordinary teardown isn't itself an error condition -- see VirNetRpcStream's class doc.
        var stream = new FakeDuplexStream();
        stream.QueueReply(serial: 1, VirNetMessageStatus.Continue, payload: []);
        var client = new VirNetRpcClient(stream);
        var rpcStream = await client.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        await rpcStream.DisposeAsync();

        var sent = stream.ReadAllSentFrames();
        Assert.Equal(2, sent.Count);
        Assert.Equal(VirNetMessageType.Stream, sent[1].Header.Type);
        Assert.Equal(VirNetMessageStatus.Ok, sent[1].Header.Status);
    }
}
