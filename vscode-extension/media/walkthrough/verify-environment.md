# Verify the environment

Run **.NET Diagnostics: Check Environment**. It starts the configured CLI for the protocol
handshake and runs `doctor --json`, then reports:

- a missing executable or missing .NET runtime,
- a CLI that is too old for protocol v2 (with the update command to copy),
- ptrace, UID, and permission problems, each with the fix the CLI suggests.

After you install or update the CLI, use **Recheck** in the notification.
