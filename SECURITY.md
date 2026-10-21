# Security boundaries

This is a community application, not an independently audited security product.
The review below does not promise that the app or its dependencies are free of
all vulnerabilities.

## Bridge connection

Version 0.4.2 removes certificate-validation bypasses from the app's Bridge
connection. HTTPS certificates must chain to one of the bundled Hue roots,
remain valid and permit TLS server authentication. The certificate's 16-digit
Bridge ID must match the ID saved when pairing. A Bridge certificate names its
ID rather than its DHCP address; the IP hostname mismatch is deliberately
replaced by this identity check.

Existing settings without an ID migrate through an unauthenticated,
certificate-validated `/api/config` request before sending the stored app key.
The returned ID must match the certificate. Initial selection still relies on
the configured local address pointing to the intended genuine Bridge. Discovery
results alone are never treated as proof of identity.

The public trust certificates in `OsuHue/Certificates/hue-roots.pem` are the
Philips Hue `root-bridge` and Signify Hue `Hue Root CA 01` roots distributed by
[OpenHue](https://github.com/openhue/openhue-go/blob/main/certs.go), which references
the [Hue HTTPS guidance](https://developers.meethue.com/develop/application-design-guidance/using-https/).
Their source licence is included in `THIRD-PARTY-NOTICES`. Certificate downloads
are disabled during validation. Online revocation checks are not performed;
compromise/revocation of a trusted Hue issuer requires a trust-store update.
Unexpected certificates fail closed; there is no accept-any-certificate switch.

Hue REST requests reject redirects and proxies and accept only private/link-local
IPv4 destinations. Entertainment activation uses that same validated HTTP client.
The SDK's `StreamingHueClient` is deliberately not used because it creates a
separate REST client that bypasses certificate validation.

Light packets use Bouncy Castle DTLS 1.2 with the paired client key and
`TLS_PSK_WITH_AES_128_GCM_SHA256`. Encryption, authentication and replay handling
are provided by the library. The app only supplies the transport and HueStream
v2 packet framing. Losing the connection or power can prevent state restoration.

**Use a trusted LAN.** DTLS 1.2 PSK identities are sent in cleartext during the
handshake ([RFC 4279, section 7.3](https://www.rfc-editor.org/rfc/rfc4279#section-7.3)).
Hue uses the app key as that identity (the SDK uses the same mapping). A party
able to capture the LAN handshake can therefore learn the app key and may use
it to control Bridge REST resources. The separate client key is not sent in
cleartext, and light packets remain authenticated/encrypted. This is a Hue
protocol limitation, not something certificate checking or replacing the crypto
library fixes. Avoid shared/untrusted networks; network isolation requires
keeping the computer-to-Bridge traffic away from untrusted devices. The chosen
plain PSK cipher also has no forward secrecy if the client key is later stolen.

## Credentials and local services

Bridge credentials are protected on disk with Windows DPAPI for the current
user. They must exist in process memory while connecting. DPAPI does not protect
against malware running as that user, an administrator or a compromised OS.
Logs omit exception messages and record operation names and exception types.
Settings, credentials, gameplay recordings and private beatmaps are not release
inputs. There is no telemetry.

tosu HTTP/WebSocket endpoints must be loopback addresses. Redirects remain on
the same origin. HTTP responses are limited to 16 MiB and WebSocket messages to
4 MiB; Bridge HTTP responses are limited to 2 MiB. The render loop blacks out
stale or mismatched data instead of continuing with an old map.

Keep tosu bound to `127.0.0.1` and do not forward its port. tosu's origin/IP
policy is not application authentication. Other programs under the same user
can contact local services. The optional timeline route returns current map
data; it does not add authentication, execute commands, accept file paths or
control lights. This app does not open an inbound server port.

## Downloads and dependencies

The installer downloads an official, fixed tosu release over HTTPS. Both the
ZIP's SHA-256 and the executable's SHA-256 are pinned. Cached executables are
checked again before launch; altered copies are downloaded again. Download and
expanded sizes are bounded. The managed tosu process starts with automatic
updates disabled so it cannot silently move beyond that verified version.
Updates to this pinned dependency require an app update. MD5 elsewhere in the
app identifies osu! beatmaps; it is not used to authenticate executable downloads.

NuGet restore uses committed lock files. The review's NuGet vulnerability scan
reported no known vulnerable packages in the configured feed. That is an
advisory check, not a proof that the older Bouncy Castle dependency has no flaws.
The self-contained .NET runtime also requires app releases for runtime updates.
Unsigned builds and SHA-256 release files do not provide publisher signatures.
A compromised publishing account, dependency, Bridge firmware or Windows user
remains outside the protections described here.

## Verification

Automated checks cover trusted/untrusted, wrong-identity, expired, wrong-purpose
and missing certificates; HueStream header/channel encoding; modified installer
payload rejection; oversized HTTP responses; parsing; stale data and lifecycle
cleanup. Test certificate private keys are generated in memory only.

A real Bridge check verified trusted HTTPS, authenticated Entertainment
activation, DTLS sending and normal stop/restoration. This was not a penetration
test or a measurement of physical colour/timing. Rapid light pulses are part of
the effect; use the stop control if the effect is uncomfortable. Software cannot
guarantee physical safety or successful restoration after network/power loss.
