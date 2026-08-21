# WinRM-over-SSH-gateway - `12152` diagnostic

*Also available in French: [fr/winrm-gateway-12152-diagnostic.md](fr/winrm-gateway-12152-diagnostic.md).*

Status: **closed - no code fix required in Heimdall.**

## Context

A WinRM profile can be routed through an SSH gateway (HTTP-only on the tunnel,
NTLM auth). On one domain environment, such a profile fails: Heimdall establishes
the SSH tunnel cleanly, but `Enter-PSSession` returns WinHTTP error **`12152`**
("the server returned an invalid or unrecognized response").

The Heimdall log shows `Tunnel established ... port 59850` with no forwarded-port
error - the `12152` surfaces downstream, inside the PowerShell terminal, not in
the Heimdall logger. An RDP profile through the *same* bastion and host works, so
the Heimdall tunnel machinery itself is sound.

Reference setup: WinRM target `winrm-target.example.internal:5985` (HTTP) via SSH bastion
`bastion.example.internal:22`.

## Manual isolation test

To separate Heimdall from the environment, the same tunnel was rebuilt by hand,
outside Heimdall, and WinRM was exercised directly against it.

```
# Pageant loaded with the bastion key, in a real console (not ISE):
plink -ssh -N -L 59850:winrm-target.example.internal:5985 <user>@bastion.example.internal
```

```powershell
# In a separate real console:
Test-NetConnection 127.0.0.1 -Port 59850
Invoke-WebRequest http://127.0.0.1:59850/wsman
$cred = Get-Credential
Enter-PSSession -ComputerName 127.0.0.1 -Port 59850 -Authentication Negotiate -Credential $cred
```

`plink` is required (not OpenSSH `ssh`): the bastion is public-key only and the
key lives in Pageant. Credential mode (NTLM) is used because tunnelling makes the
Kerberos SPN `HTTP/127.0.0.1`, which has no ticket.

## Observed result

| Layer | Outcome |
|---|---|
| TCP tunnel | **OK** - `Test-NetConnection` reports `TcpTestSucceeded: True` |
| HTTP / WinRM | **Fails** - `Invoke-WebRequest .../wsman`: "The underlying connection was closed: An unexpected error occurred on a receive." |
| `Enter-PSSession` | **Fails** - `PSRemotingTransportException` |

The TCP forward reaches the target, but the HTTP/WinRM exchange is closed
unexpectedly.

## Conclusion

The failure reproduces **outside Heimdall**, with a hand-built tunnel. Heimdall
is not in the path of the fault. The cause is environmental: the WinRM service on
the target, or an application-layer device along the `bastion -> winrm-target.example.internal:5985`
path, terminates the HTTP session.

## Reading the `plink` console

The line `plink` prints right after the failed attempt attributes the fault
between the two links of the chain:

| `plink` message | Meaning |
|---|---|
| `administratively prohibited` | The bastion SSH server refuses forwarding to the target (`AllowTcpForwarding` / ACL). Failure is at the SSH layer. |
| `connection refused` | The bastion reaches the target but port 5985 returns a RST - service not bound to that interface, or a firewall. |
| `remote host closed` / `remote side closed connection` | The forward opens, then the target closes the connection mid-exchange - points to the WinRM service or an application-layer IPS/proxy. |
| *(no line)* | The SSH forward is healthy; the fault is purely the `winrm-target.example.internal:5985` service. |

In every case the "outside Heimdall" conclusion stands - this line only
attributes the fault, it does not change the verdict.

## Status

No code fix required in Heimdall. WinRM-over-SSH-gateway is delivered and behaves
correctly; this is an environment-specific limitation of one target.
