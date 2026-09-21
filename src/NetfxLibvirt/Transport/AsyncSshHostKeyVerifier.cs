namespace NetfxLibvirt.Transport;

/// <summary>Asynchronous counterpart to <see cref="SshHostKeyVerifier"/> — for
/// callers that need to do real asynchronous work before deciding whether
/// to trust a host key, most importantly asking the user ("Trust this host?
/// SHA256:...") during the handshake rather than blindly auto-pinning on
/// first use. Return <see langword="true"/> to trust the key,
/// <see langword="false"/> to reject it (surfaced to the caller of
/// <see cref="SshTransport.ConnectAsync"/> / <see cref="SshPortForward.OpenAsync"/>
/// as a <see cref="SshHostKeyRejectedException"/>).
///
/// **Honor <paramref name="cancellationToken"/>.** It's the token given to
/// <c>ConnectAsync</c>/<c>OpenAsync</c>, so cancelling the connect is how a
/// pending prompt gets dismissed. The connect stops waiting the moment the
/// token is cancelled even if the verifier ignores it, but a verifier that
/// ignores it leaves its own prompt orphaned on screen.
///
/// The verifier is invoked on a thread-pool thread with no
/// <see cref="SynchronizationContext"/> — never on the SSH library's
/// connection thread and never under the caller's context — so a UI
/// consumer can freely marshal to its own dispatcher (e.g. show a dialog
/// and await the result) without risk of deadlocking against the thread the
/// handshake is blocked on. Throwing rejects the key; cancellation (with the
/// connect's token) surfaces as <see cref="OperationCanceledException"/>,
/// any other exception as <see cref="SshTransportException"/> wrapping it.
///
/// **The handshake waits while the verifier runs**, and SSH.NET bounds that
/// wait with its connection timeout (30 seconds by default) — an
/// interactive verifier should set <see cref="SshTransportOptions.ConnectTimeout"/>
/// to comfortably cover how long a person might take to answer.</summary>
public delegate ValueTask<bool> AsyncSshHostKeyVerifier(SshHostKeyInfo hostKey, CancellationToken cancellationToken);
