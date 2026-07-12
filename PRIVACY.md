# SpaceLens privacy policy

Effective date: July 12, 2026

SpaceLens is a local Windows disk-space analyzer. It does not contain advertising, analytics, telemetry, user accounts, or a remote scanning service.

## Data that stays on the computer

Scanning and categorization happen on the user's computer. File and folder paths, names, sizes, timestamps, file identities, scan totals, saved scan results, update preferences, and crash logs are not uploaded by SpaceLens.

SpaceLens stores saved scan data, its update preference, and any crash log under `%LOCALAPPDATA%\SpaceLens`. The normal uninstaller offers to remove this saved data. Files selected for cleanup are passed to the local Windows Recycle Bin; SpaceLens does not send their contents anywhere.

SpaceLens 1.6.1 does not start an Administrator helper. The former Full access scan is retired/temporarily unavailable while a native broker is designed. Standard scanning, cleanup, updates, installation, and uninstall remain local and unelevated.

## Network activity

SpaceLens contacts only the official `Purxy8/SpaceLens` GitHub release endpoints, and only for software updates:

- If **Check for updates automatically** is enabled, SpaceLens requests the signed update manifest at most once every 24 hours after the app is opened.
- Pressing **Check for updates** makes the same request immediately, even if automatic checks are disabled.
- An installer is downloaded only after SpaceLens shows the available version and the user confirms the download.
- Opening a documentation or release link is always a user-requested action and launches the default browser.

An update request contains the normal information needed for an HTTPS connection, including the user's public IP address as observed by GitHub, and the HTTP user-agent `SpaceLens/<version>`. It does not contain scanned paths, file names, scan results, cleanup selections, crash logs, or a persistent SpaceLens identifier. GitHub processes connection data under the [GitHub Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).

## Controlling automatic checks

The installer displays the automatic-update option before making changes. Clear it to install with automatic checks disabled. When this Windows account has no previously saved SpaceLens preference, a portable copy starts with automatic checks disabled. Installed and portable copies for the same Windows account share that local preference. In SpaceLens, right-click **Check for updates** and use **Check automatically once a day** to change it at any time. An unreadable or damaged preference fails closed to automatic checks being off. Manual checks remain available.

## Changes and questions

Material changes to this policy will be committed to the public repository. Security concerns should be reported using the private process described in [SECURITY.md](SECURITY.md). Other questions can be opened as a GitHub issue without including private file or system information.
