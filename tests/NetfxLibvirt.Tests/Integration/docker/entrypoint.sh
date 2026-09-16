#!/bin/sh
set -e

/usr/sbin/sshd

# Foreground, becomes the container's main process. auth_unix_rw defaults
# to "none" on a stock install — no libvirt-side credentials needed for
# the local socket virt-ssh-helper (exec'd over the SSH connection above)
# talks to.
exec /usr/sbin/libvirtd
