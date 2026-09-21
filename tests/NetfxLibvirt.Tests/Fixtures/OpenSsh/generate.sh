#!/bin/bash
# Regenerates every fixture here with the REAL ssh-keygen (Git for Windows /
# Windows OpenSSH / any OpenSSH >= 8.2). Only public halves are kept: the CA
# and host private keys are throwaway and deleted at the end (except the two noted below), so regenerating
# produces a fresh, self-consistent set (new CAs, new host keys, all certs and
# known_hosts files re-derived together). Nothing here is a credential for
# anything real.
#
#   bash generate.sh
set -euo pipefail
cd "$(dirname "$0")"
find . -maxdepth 1 -type f ! -name generate.sh ! -name README.md -delete
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT

HOST=lab.example
gen() { ssh-keygen -q -t "$1" ${3:+-b "$3"} -N '' -C "$2" -f "$work/$2"; }

gen ed25519 ca_ed25519
gen rsa     ca_rsa 3072
gen ecdsa   ca_ecdsa 384
gen ed25519 ca_other
gen ed25519 host_ed25519
gen rsa     host_rsa 3072
gen ecdsa   host_ecdsa 256
gen ed25519 attacker_ed25519

# sign <ca> <hostkey> <out-name> <ssh-keygen args...>; the cert comes out as <hostkey>-cert.pub next to the key
sign() {
  local ca=$1 key=$2 out=$3; shift 3
  cp "$work/$key.pub" "$work/sign_$out.pub"
  ssh-keygen -q -s "$work/$ca" -I "id-$out" "$@" "$work/sign_$out.pub"
  cp "$work/sign_$out-cert.pub" "cert-$out.pub"
}

# --- one valid host cert per CA key type (host key: ed25519) ---
sign ca_ed25519 host_ed25519 valid-ed25519ca -h -n $HOST -z 1
sign ca_rsa     host_ed25519 valid-rsaca     -h -n $HOST -z 2
sign ca_ecdsa   host_ed25519 valid-ecdsaca   -h -n $HOST -z 3
# --- other certified key types ---
sign ca_ed25519 host_rsa     valid-rsahostkey   -h -n $HOST
sign ca_ed25519 host_ecdsa   valid-ecdsahostkey -h -n $HOST
# --- the things that must be rejected / handled ---
sign ca_ed25519 host_ed25519 expired          -h -n $HOST -V 20200101:20200102
sign ca_ed25519 host_ed25519 notyet           -h -n $HOST -V 20980101:20990101
sign ca_ed25519 host_ed25519 wrong-principal  -h -n other.example
sign ca_ed25519 host_ed25519 empty-principals -h
sign ca_ed25519 host_ed25519 wildcard-principal -h -n '*.example'
sign ca_ed25519 host_ed25519 user-cert        -n $HOST          # NOT -h: a user certificate
sign ca_other   host_ed25519 wrong-ca         -h -n $HOST
sign ca_ed25519 host_ed25519 critical-option  -h -n $HOST -O force-command=/bin/true
sign ca_rsa     host_ed25519 sha1-ca          -h -n $HOST -t ssh-rsa   # CA signature is SHA-1 (ssh-rsa)

# --- tampered: flip the last byte of the (valid) certificate's CA signature ---
line=$(cat cert-valid-ed25519ca.pub); type=${line%% *}; rest=${line#* }; b64=${rest%% *}
echo "$b64" | base64 -d > "$work/c.bin"
len=$(wc -c < "$work/c.bin")
last=$(od -An -tu1 -j$((len-1)) -N1 "$work/c.bin" | tr -d ' ')
head -c $((len-1)) "$work/c.bin" > "$work/t.bin"
hex=$(printf '%02x' $(( last ^ 255 )))
printf "\x$hex" >> "$work/t.bin"
[ "$(wc -c < "$work/t.bin")" -eq "$len" ] || { echo "tamper produced wrong length" >&2; exit 1; }
echo "$type $(base64 -w0 "$work/t.bin") tampered" > cert-tampered.pub

# --- public keys ---
# The two PRIVATE keys kept: host_ed25519 (the key every cert-*.pub certifies, except the rsa/ecdsa-hostkey ones) and
# attacker_ed25519 (a key no certificate certifies). Throwaway, used to make key-exchange-style signatures in
# SshNetCertificateVerificationSentinelTests. Not credentials for anything.
cp "$work/host_ed25519" host_ed25519
cp "$work/attacker_ed25519" attacker_ed25519
for k in ca_ed25519 ca_rsa ca_ecdsa ca_other host_ed25519 host_rsa host_ecdsa; do cp "$work/$k.pub" "$k.pub"; done

# --- known_hosts files ---
# key(k): "<type> <base64>" of a .pub, no comment
key() { awk '{print $1" "$2}' "$1"; }
{
  echo "# plain known_hosts fixture"
  echo
  echo "$HOST,alias.example $(key host_ed25519.pub) comment for lab"
  echo "[$HOST]:2222 $(key host_rsa.pub)"
  echo "*.wild.example,!bad.wild.example $(key host_ecdsa.pub)"
  echo "onlyrsa.example $(key host_rsa.pub)"
  echo "this line is not valid"
  echo "@bogus-marker x.example $(key host_ed25519.pub)"
} > known_hosts_plain
cp known_hosts_plain "$work/kh_to_hash"
{ echo "$HOST $(key host_ed25519.pub)"; echo "[$HOST]:2222 $(key host_rsa.pub)"; } > "$work/kh_to_hash"
ssh-keygen -q -H -f "$work/kh_to_hash" >/dev/null 2>&1 || true
cp "$work/kh_to_hash" known_hosts_hashed
{
  echo "@cert-authority *.example $(key ca_ed25519.pub) lab CA"
  echo "@cert-authority [$HOST]:2222 $(key ca_rsa.pub)"
} > known_hosts_ca
{
  echo "@revoked * $(key host_ed25519.pub)"
  echo "@revoked other.example $(key host_rsa.pub)"
} > known_hosts_revoked
echo "generated:"; ls
