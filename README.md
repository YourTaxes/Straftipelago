# Straftipelago
A mod for the game Straftat that gives it integration with archipelago multiworlds.

## Requirements

- **[MyceliumNetworking for STRAFTAT](https://thunderstore.io/c/straftat/)** — required, not
  optional. Every player who picks up a Roulette Item rolls a weapon from their own local pool
  and needs to ask the host to spawn it; Mycelium carries that one message. Without it the
  roulette rolls and then does nothing, leaving the item stuck in your hand.

  The mod declares this with `[BepInDependency]`, so BepInEx will refuse to load Straftipelago
  if Mycelium is missing rather than failing halfway through a match.

  (The game's own networking cannot do this. It has the right RPC — `PlayerSpawnObject
  .SpawnObject` — but a client may only invoke a ServerRpc on a NetworkObject it owns, and
  that component is not on the player prefab. See the class comment in `Utils/RouletteNet.cs`
  for the full search and the one remaining pure-vanilla alternative.)

- This mod is **not vanilla-compatible** (`[assembly: StraftatMod(isVanillaCompatible: false)]`),
  so every player in the lobby needs it. The "Incompatible assemblies found" warning at startup
  is expected and benign.
