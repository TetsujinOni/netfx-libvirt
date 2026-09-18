using NetfxLibvirt.Generated.Remote;
using NetfxLibvirt.Rpc;
using NetfxLibvirt.Xdr;

namespace NetfxLibvirt;

/// <summary>
/// An open libvirt RPC session over an already-connected transport
/// <see cref="Stream"/> (Unix socket, TCP, TLS, or an SSH tunnel — this type
/// doesn't care which; see <c>docs/plan.md</c> for the transports
/// themselves). Construct with <see cref="OpenAsync"/>, which performs the
/// handshake every libvirt client must do before anything else: an
/// <c>AUTH_LIST</c> call (libvirt requires this even when no authentication
/// is actually used), then <c>CONNECT_OPEN</c>.
///
/// The operations below (<see cref="ListDomainsAsync"/>,
/// <see cref="StartDomainAsync"/>, <see cref="ShutdownDomainAsync"/>,
/// <see cref="DestroyDomainAsync"/>, <see cref="GetDomainXmlAsync"/>,
/// <see cref="DisconnectAsync"/>) are deliberately the same surface as
/// <c>virt-desktop</c>'s own <c>hypervisorAPI</c> interface
/// (<c>hypervisor.go</c>) — this project's current parity milestone, see
/// <c>docs/plan.md</c>.
/// </summary>
public sealed class LibvirtConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly VirNetRpcClient _rpc;
    private bool _disconnected;

    private LibvirtConnection(Stream stream, VirNetRpcClient rpc)
    {
        _stream = stream;
        _rpc = rpc;
    }

    /// <summary>
    /// Performs the AUTH_LIST + CONNECT_OPEN handshake over
    /// <paramref name="stream"/> and returns the open session.
    /// <paramref name="uri"/> is the libvirt connection URI (e.g.
    /// <c>qemu:///system</c>) — it identifies the driver on the other end,
    /// independent of how <paramref name="stream"/> itself got there.
    ///
    /// **Never pass a transport-prefixed URI here** (e.g.
    /// <c>qemu+ssh://user@host/system</c>) even when that's the URI the
    /// caller's own connection started from — <paramref name="uri"/> is
    /// what the already-connected daemon uses to pick a driver, not how the
    /// caller reached it, and the daemon has no reason to know or care
    /// about the transport. Real libvirt clients strip the transport/host
    /// before sending this (see <c>remoteConnectFormatURI</c> in libvirt's
    /// own <c>src/remote/remote_driver.c</c>) — passing the untouched
    /// transport URI through by mistake doesn't just get rejected cleanly:
    /// against a real <c>qemu</c> driver it can make libvirtd try to
    /// interpret the string as *its own* SSH target and attempt a genuine
    /// outbound SSH connection, failing with a real but wildly misleading
    /// "host key verification failed" error that has nothing to do with the
    /// caller's actual connection. Caught for real during story 12's
    /// real-lab-host validation (<c>docs/plan.md</c>) — confirmed by
    /// reproducing and then fixing the exact mistake in a validation
    /// script, not by inspection alone.
    ///
    /// Only <see cref="RemoteAuthType.RemoteAuthNone"/> is supported so far
    /// — matches every transport in <c>docs/plan.md</c>'s parity milestone
    /// (a local Unix socket, or an SSH tunnel that already authenticated at
    /// the SSH layer). SASL/Polkit are real gaps, not silently accepted:
    /// this throws <see cref="NotSupportedException"/> naming what
    /// libvirtd actually offered instead of guessing.
    /// </summary>
    public static async Task<LibvirtConnection> OpenAsync(Stream stream, string uri, CancellationToken cancellationToken = default)
    {
        var rpc = new VirNetRpcClient(stream);

        var authListPayload = await rpc.CallAsync((int)RemoteProcedure.RemoteProcAuthList, ReadOnlyMemory<byte>.Empty, cancellationToken)
            .ConfigureAwait(false);
        var authList = RemoteAuthListRet.Decode(new XdrReader(authListPayload));
        if (!authList.Types.Contains(RemoteAuthType.RemoteAuthNone))
        {
            throw new NotSupportedException(
                $"libvirtd only offered [{string.Join(", ", authList.Types)}] — only {nameof(RemoteAuthType.RemoteAuthNone)} is supported so far (see docs/plan.md).");
        }

        var openArgs = new RemoteConnectOpenArgs { Name = uri, Flags = 0 };
        var argsWriter = new XdrWriter();
        openArgs.Encode(argsWriter);
        await rpc.CallAsync((int)RemoteProcedure.RemoteProcConnectOpen, argsWriter.ToArray(), cancellationToken).ConfigureAwait(false);

        return new LibvirtConnection(stream, rpc);
    }

    /// <summary>Lists every domain (active and inactive), each paired with
    /// its current state via a separate <c>DOMAIN_GET_STATE</c> call —
    /// mirrors <c>hypervisor.go</c>'s own <c>ListVMs</c> exactly, including
    /// making N+1 calls rather than one, because that's what the real
    /// protocol requires (<c>CONNECT_LIST_ALL_DOMAINS</c> doesn't return
    /// state).</summary>
    public async Task<IReadOnlyList<DomainSummary>> ListDomainsAsync(CancellationToken cancellationToken = default)
    {
        var listArgs = new RemoteConnectListAllDomainsArgs
        {
            NeedResults = 1,
            Flags = (uint)(ConnectListAllDomainsFlags.Active | ConnectListAllDomainsFlags.Inactive),
        };
        var listWriter = new XdrWriter();
        listArgs.Encode(listWriter);
        var listPayload = await _rpc.CallAsync((int)RemoteProcedure.RemoteProcConnectListAllDomains, listWriter.ToArray(), cancellationToken)
            .ConfigureAwait(false);
        var listRet = RemoteConnectListAllDomainsRet.Decode(new XdrReader(listPayload));

        var summaries = new List<DomainSummary>(listRet.Domains.Count);
        foreach (var domain in listRet.Domains)
        {
            var stateArgs = new RemoteDomainGetStateArgs { Dom = domain, Flags = 0 };
            var stateWriter = new XdrWriter();
            stateArgs.Encode(stateWriter);
            var statePayload = await _rpc.CallAsync((int)RemoteProcedure.RemoteProcDomainGetState, stateWriter.ToArray(), cancellationToken)
                .ConfigureAwait(false);
            var stateRet = RemoteDomainGetStateRet.Decode(new XdrReader(statePayload));

            var state = Enum.IsDefined(typeof(DomainState), stateRet.State) ? (DomainState)stateRet.State : DomainState.Unknown;
            summaries.Add(new DomainSummary(domain.Name, domain.Uuid, domain.Id, state));
        }

        return summaries;
    }

    /// <summary>Boots an inactive domain — mirrors <c>hypervisor.go</c>'s <c>StartVM</c>.</summary>
    public async Task StartDomainAsync(string name, CancellationToken cancellationToken = default)
    {
        var domain = await LookupDomainByNameAsync(name, cancellationToken).ConfigureAwait(false);
        var args = new RemoteDomainCreateArgs { Dom = domain };
        var writer = new XdrWriter();
        args.Encode(writer);
        await _rpc.CallAsync((int)RemoteProcedure.RemoteProcDomainCreate, writer.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Issues an ACPI shutdown signal to the guest OS — mirrors
    /// <c>hypervisor.go</c>'s <c>ShutdownVM</c>. Graceful; the guest OS
    /// decides when (or whether) to actually power off.</summary>
    public async Task ShutdownDomainAsync(string name, CancellationToken cancellationToken = default)
    {
        var domain = await LookupDomainByNameAsync(name, cancellationToken).ConfigureAwait(false);
        var args = new RemoteDomainShutdownArgs { Dom = domain };
        var writer = new XdrWriter();
        args.Encode(writer);
        await _rpc.CallAsync((int)RemoteProcedure.RemoteProcDomainShutdown, writer.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Forcefully terminates the domain's execution — mirrors
    /// <c>hypervisor.go</c>'s <c>PowerOffVM</c> (the doc comment there says
    /// it best: "equivalent to pulling power cord").</summary>
    public async Task DestroyDomainAsync(string name, CancellationToken cancellationToken = default)
    {
        var domain = await LookupDomainByNameAsync(name, cancellationToken).ConfigureAwait(false);
        var args = new RemoteDomainDestroyArgs { Dom = domain };
        var writer = new XdrWriter();
        args.Encode(writer);
        await _rpc.CallAsync((int)RemoteProcedure.RemoteProcDomainDestroy, writer.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Retrieves the domain's XML description — mirrors
    /// <c>hypervisor.go</c>'s <c>GetVMXML</c>.</summary>
    public async Task<string> GetDomainXmlAsync(string name, CancellationToken cancellationToken = default)
    {
        var domain = await LookupDomainByNameAsync(name, cancellationToken).ConfigureAwait(false);
        var args = new RemoteDomainGetXmlDescArgs { Dom = domain, Flags = 0 };
        var writer = new XdrWriter();
        args.Encode(writer);
        var payload = await _rpc.CallAsync((int)RemoteProcedure.RemoteProcDomainGetXmlDesc, writer.ToArray(), cancellationToken).ConfigureAwait(false);
        return RemoteDomainGetXmlDescRet.Decode(new XdrReader(payload)).Xml;
    }

    /// <summary>
    /// Opens a tunnel to a domain's graphics server (VNC or SPICE, per its
    /// <c>&lt;graphics&gt;</c> XML element) over this *existing* RPC
    /// connection — the real fix for the common case where the graphics
    /// server only listens on the hypervisor's loopback interface
    /// (<c>&lt;graphics ... listen='127.0.0.1'&gt;</c>, libvirt's actual
    /// default): dialing the hypervisor's routable IP at that port directly
    /// gets connection-refused, because nothing outside the hypervisor can
    /// reach it that way. <c>REMOTE_PROC_DOMAIN_OPEN_GRAPHICS</c> exists
    /// specifically so a remote client doesn't need a second connection (an
    /// SSH port-forward, etc.) — it tunnels the graphics protocol's raw
    /// bytes as ordinary stream data on this same connection instead, which
    /// is how <c>virt-manager</c> itself reaches a loopback-bound graphics
    /// server remotely.
    ///
    /// <paramref name="index"/> is the graphics device's index within the
    /// domain (<c>0</c> for the common single-graphics-device case — matches
    /// <c>go-libvirt</c>'s own <c>DomainOpenGraphics(Dom, Idx, Flags)</c>
    /// signature). The returned <see cref="Stream"/> carries the raw
    /// VNC/SPICE protocol bytes in both directions — read/write it exactly
    /// as you would a direct socket to the graphics server; see
    /// <see cref="VirNetRpcStream"/>'s own doc for the framing this tunnels
    /// over. While this stream is open, no other call can be made on this
    /// <see cref="LibvirtConnection"/> (see <see cref="VirNetRpcClient"/>'s
    /// doc) — dispose it before, e.g., calling <see cref="GetDomainXmlAsync"/>
    /// again.
    /// </summary>
    public async Task<VirNetRpcStream> OpenGraphicsAsync(string name, uint index = 0, CancellationToken cancellationToken = default)
    {
        var domain = await LookupDomainByNameAsync(name, cancellationToken).ConfigureAwait(false);
        var args = new RemoteDomainOpenGraphicsArgs { Dom = domain, Idx = index, Flags = 0 };
        var writer = new XdrWriter();
        args.Encode(writer);
        return await _rpc.OpenStreamAsync((int)RemoteProcedure.RemoteProcDomainOpenGraphics, writer.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gracefully ends the RPC session (<c>CONNECT_CLOSE</c>) and
    /// closes the transport stream — mirrors <c>hypervisor.go</c>'s
    /// <c>Disconnect</c>. Idempotent. Unlike <see cref="DisposeAsync"/>,
    /// this propagates a failed close instead of swallowing it, since a
    /// caller who explicitly asked to disconnect should find out if it
    /// didn't work cleanly.</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disconnected)
        {
            return;
        }

        _disconnected = true;
        await _rpc.CallAsync((int)RemoteProcedure.RemoteProcConnectClose, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Best-effort <see cref="DisconnectAsync"/>: attempts the same
    /// graceful <c>CONNECT_CLOSE</c>, but — matching general .NET dispose
    /// guidance — never throws even if that fails, and always closes the
    /// transport stream regardless. Prefer calling <see cref="DisconnectAsync"/>
    /// explicitly when the caller wants to know whether the close
    /// succeeded.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disconnected)
        {
            return;
        }

        _disconnected = true;
        try
        {
            await _rpc.CallAsync((int)RemoteProcedure.RemoteProcConnectClose, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: Dispose must not throw even if the graceful RPC close fails.
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<RemoteNonnullDomain> LookupDomainByNameAsync(string name, CancellationToken cancellationToken)
    {
        var args = new RemoteDomainLookupByNameArgs { Name = name };
        var writer = new XdrWriter();
        args.Encode(writer);
        var payload = await _rpc.CallAsync((int)RemoteProcedure.RemoteProcDomainLookupByName, writer.ToArray(), cancellationToken).ConfigureAwait(false);
        return RemoteDomainLookupByNameRet.Decode(new XdrReader(payload)).Dom;
    }
}
