# Prebuild

Builds the ready-made `ManagerAgent.dll` shipped inside `ManagerAgent-Setup.exe`, without needing a copy of the game.

`stubs/` holds stand-ins for BepInEx, Il2CppInterop and the Unity interop assemblies. They declare only what the
plugin uses, with the same assembly names, namespaces, declaring types and signatures as the real ones (checked
against the BepInEx 6 and Il2CppInterop sources), at version 0.0.0.0 so any real version satisfies them.
They are compile-time only and never shipped.

The plugin's startup self-test confirms the match on each player's machine; if it ever fails, Setup's Repair
builds the plugin from source against that player's real game files.
