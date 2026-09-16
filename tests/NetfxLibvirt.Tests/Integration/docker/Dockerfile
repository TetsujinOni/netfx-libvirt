# Throwaway image for NetfxLibvirt.Tests' real-infra integration tests
# (docs/plan.md stories 6-12). Built fresh by Testcontainers on every test
# run — never published, never a real deployment target.
#
# Ubuntu 22.04 deliberately: it's the same libvirt 8.0.0 baseline this
# project's compatibility bisection validated (see reference/README.md),
# so this container is a faithful stand-in for the real WSL environment
# those docs describe, not a synthetic shortcut.
#
# No qemu-system/kvm packages, no --privileged, no /dev/kvm passthrough:
# these tests only ever connect to libvirt's built-in test:///default
# null-hypervisor driver, which needs none of that (see LibvirtConnection's
# own tests and docs/plan.md's transport section for why that driver was
# chosen for exactly this reason).
FROM ubuntu:22.04

RUN apt-get update && \
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        libvirt-daemon-system \
        openssh-server \
    && rm -rf /var/lib/apt/lists/*

# Test-only key pair, committed alongside this Dockerfile (id_ecdsa /
# id_ecdsa.pub — ECDSA because Microsoft.DevTunnels.Ssh.Keys' OpenSSH
# importer doesn't support ed25519, only RSA/ECDSA) — authenticates into
# THIS throwaway, self-built container only. Not a credential for anything
# real; see this directory's README.
COPY id_ecdsa.pub /root/.ssh/authorized_keys
RUN mkdir -p /run/sshd && \
    chmod 700 /root/.ssh && \
    chmod 600 /root/.ssh/authorized_keys

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

EXPOSE 22
ENTRYPOINT ["/entrypoint.sh"]
