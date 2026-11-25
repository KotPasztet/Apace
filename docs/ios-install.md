# Installing on iOS: sideload the patched IPA

The Patcher page produces a patched `Payload/*.app` IPA for Minecraft Earth 0.33.0
(iOS). To run it on a real device the app must be **signed**, and the only signature
an ordinary iPhone accepts outside the App Store is one made with a personal Apple ID
(free developer certificate, 7-day validity) or a paid one (365 days). Apace does not
sign anything itself — the sideload tool does that as part of installing, which makes
installing the patched client a one-step flow:

1. Patch your original IPA in the panel's **Client Patcher** (iOS tab).
2. Download the patched IPA.
3. Sideload it with **Sideloadly** (Windows/macOS) or **SideStore/AltStore**
   ( SideStore pairs the phone with a helper app; AltStore needs a Mac or a PC once).
   Both tools take your Apple ID, re-sign every binary in the app with your
   certificate and install it. No Mac codesign script, no manual provisioning
   profile, no other steps.

After the 7-day certificate expires the sideload tool re-signs the same IPA
(re-patching is not needed — the patch is baked into the file).

## Why the patcher embeds keychain entitlements (the XAL part)

iOS only runs signed code, and an app's entitlements live **inside** its code
signature. Xbox sign-in (Microsoft's XAL library, which the login page flow uses)
stores its tokens in the iOS keychain under the access groups
`com.microsoft.xal` and `com.microsoft.adalcache`; iOS only lets the app touch
those groups if the two strings are present in its code entitlements — re-prefixed
with the signing team's id, which a team wildcard profile covers.

The original App Store signature carries Mojang's entitlements, but patching means
stripping that signature (`Remove DRM`). From then on the app has no entitlements
at all, and XAL fails before it sends a single request — the client just shows
"something went wrong during sign in" without ever opening a login page. That is
why the patcher pseudo-signs the main executable and the `Xal` framework binary
with `ldid`, embedding entitlements that contain exactly those two keychain
groups. Sideloadly/SideStore read the entitlements embedded in the binaries when
they re-sign the app and re-apply them under your own team id — so the sign-in
works with no extra work. (The `Entitlements.plist` file the patcher also writes
into the bundle is only a reference for manual `codesign` workflows; iOS itself
reads entitlements from the signature.)

If the host cannot fetch `ldid` (see the job log), the IPA still installs — it just
loses that guarantee, and sign-in then needs the entitlements-carrying re-sign
performed by the sideload tool anyway (SideStore/AltStore keep embedded
entitlements; for Sideloadly use its "keep original entitlements" behavior).

## Server side

The patched client reaches the Apace server through the endpoints rewritten by the
patcher (locator, XAL/SISU sign-in, Xbox Live and PlayFab). Nothing else needs to
be configured on the server; the login mode shown on the panel's home page applies
to Android and iOS alike. When testing sign-in problems, set `APACE_LOG_AUTH_BODIES=1`
on the ApiServer to log the full auth traffic (tokens are redacted).
