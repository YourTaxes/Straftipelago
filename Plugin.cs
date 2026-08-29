using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Archipelago;
using Straftapelago.Finnegan_McD.org.Utils;
using UnityEngine;
using ComputerysModdingUtilities;
using Straftapelago.Finnegan_McD.org.Patches;



[assembly: StraftatMod(isVanillaCompatible: false)]

namespace Straftapelago.Finnegan_McD.org;



// Mycelium carries the roulette roll from the player who made it to the host. It is required,
// not optional: without it a picked-up Roulette Item rolls a weapon and then has no way to ask
// the server to spawn it. See RouletteNet for why the game's own RPCs cannot do this.
//
// Mod Menu is required for the same kind of reason: it hosts this mod's only login UI (see
// ArchipelagoMenu, which replaced the IMGUI connect form), so without it there is no way to
// reach an Archipelago room at all. A hard dependency makes that a single explanatory line in
// the chainloader log rather than a mod that loads and then silently cannot connect.
//
// ChatCommands is required because it IS the Archipelago console: its command registry carries
// the !commands the player types, and its chat printer is where the room's replies appear.
// Being a hard dependency also fixes load order in our favour - BepInEx runs a dependency's
// Awake before ours, so its registry exists by the time we add commands to it in Awake.
[BepInDependency(MyceliumDependencyGUID)]
[BepInDependency(ModMenuDependencyGUID)]
[BepInDependency(ChatCommandsDependencyGUID)]
[BepInPlugin(PluginGUID, PluginName, PluginVersion)]
public class Plugin : BaseUnityPlugin
{
    public const string MyceliumDependencyGUID = "RugbugRedfern.MyceliumNetworking";
    public const string ModMenuDependencyGUID = "kestrel.straftat.modmenu";
    public const string ChatCommandsDependencyGUID = "kestrel.straftat.chatcommands";

    public const string PluginGUID = "org.Finnegan_McD.Straftapelago";
    public const string PluginName = "Straftapelago.Finnegan_McD.org";
    public const string PluginVersion = "1.0.0";

    public const string ModDisplayInfo = $"{PluginName} v{PluginVersion}";
    public static ManualLogSource BepinLogger;
    public static ArchipelagoClient ArchipelagoClient;
    public static GameObject RouletteItemPrefab;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private const int STD_INPUT_HANDLE = -10;
    private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;

    // BepInEx's console (and vanilla Debug.Log/print, which it also captures) writes to
    // CONIN$/STD_INPUT_HANDLE's underlying console window. Windows consoles default to
    // QuickEdit Mode, which suspends the whole process's writes to that console the moment
    // the window is focused/clicked into selection state, until Enter/Esc is pressed or the
    // selection is cancelled — since Unity's game loop runs on the same thread doing the
    // logging, that write blocking freezes the entire game. Clearing the flag here (with
    // ENABLE_EXTENDED_FLAGS set, which Windows requires to be present for the QuickEdit bit
    // to take effect at all) prevents that hang for the lifetime of the console window.
    private static void DisableQuickEdit()
    {
        IntPtr handle = GetStdHandle(STD_INPUT_HANDLE);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
        if (!GetConsoleMode(handle, out uint mode)) return;

        mode &= ~ENABLE_QUICK_EDIT_MODE;
        mode |= ENABLE_EXTENDED_FLAGS;
        SetConsoleMode(handle, mode);
    }

    private void Awake()
    {
        // Unity swallows nothing here, but BepInEx only surfaces a failed Awake() as a
        // terse chainloader line — and a partially-initialized plugin then fails in
        // confusing ways later (OnGUI drawing against a null ArchipelagoClient, patches
        // never applied, etc). Catch and log the whole thing so a load failure is
        // obvious in LogOutput.log instead of silent. Logged via the inherited `Logger`
        // rather than the static BepinLogger field, so a throw that happens before (or
        // during) `BepinLogger = Logger;` still gets reported instead of being masked by
        // a secondary NullReferenceException.
        try
        {
            //DisableQuickEdit();

            // Plugin startup logic
            BepinLogger = Logger;
            BepinLogger.LogInfo("Mod Started - this is the print statement");

            // First thing after the logger: this binds the config, which creates/updates
            // BepInEx/config/org.Finnegan_McD.Straftapelago.cfg, and every later step here
            // (and every patch) may read an entry, so nothing may run before it. Once only:
            // Mod Menu's RegisterContentBuilder throws on a second call from this assembly.
            try
            {
                ArchipelagoMenu.Install(Config);
            }
            catch (Exception e)
            {
                BepinLogger.LogError($"Failed to register the Mod Menu page: {e}");
            }

            using (Stream stream = typeof(Plugin).Assembly.GetManifestResourceStream(
                "Straftapelago.Finnegan_McD.org.AssetBundles.roulette_item"))
            {
                if (stream != null)
                {
                    byte[] data = new byte[stream.Length];
                    stream.Read(data, 0, data.Length);
                    AssetBundle bundle = AssetBundle.LoadFromMemory(data);
                    if (bundle != null)
                    {
                        RouletteItemPrefab = bundle.LoadAsset<GameObject>("roulette_item");
                        if (RouletteItemPrefab == null)
                        {
                            BepinLogger.LogError("Asset 'roulette_item' not found in bundle. Assets present: " +
                                string.Join(", ", bundle.GetAllAssetNames()));
                        }
                    }
                    else
                        BepinLogger.LogError("Failed to load asset bundle from embedded resource");
                }
                else
                {
                    BepinLogger.LogError("Embedded resource 'roulette_item' not found");
                }
            }
            try
            {
                ArchipelagoClient = new ArchipelagoClient();
            } catch (Exception e)
            {
                BepinLogger.LogError($"Failed to initialize ArchipelagoClient: {e}");
            }
            // After the client exists, because the commands send through it. ChatCommands
            // is a hard dependency, so its registry is already up by the time this runs.
            try
            {
                ArchipelagoChatCommands.Install();
            }
            catch (Exception e)
            {
                BepinLogger.LogError($"Failed to register the Archipelago chat commands: {e}");
            }

            try
            {
                ArchipelagoConsole.Awake();
            } catch (Exception e)
            {
                BepinLogger.LogError($"Failed to initialize ArchipelagoConsole: {e}");
            }
            
            // Before PatchAll, so the roulette's RPCs are registered by the time any patch
            // could fire. Mycelium is loaded ahead of this plugin by the BepInDependency above.
            try
            {
                RouletteNet.Install();
            }
            catch (Exception e)
            {
                BepinLogger.LogError($"Failed to register roulette RPCs with Mycelium: {e}");
            }

            Harmony harmony = new Harmony(PluginGUID);
            harmony.PatchAll();

            // Separate from PatchAll, and guarded, because these targets are
            // discovered by searching the game's IL rather than named in an
            // attribute: there are dozens of them, and PatchAll is all-or-nothing
            // - one method Harmony cannot patch would throw out of here and leave
            // the whole mod half-loaded. Installed one at a time instead, so a bad
            // target costs only the kill-feed detail it would have provided.
            try
            {
                KillDetectScopes.Install(harmony);
            }
            catch (Exception error)
            {
                BepinLogger.LogError($"Failed to install kill-detection scopes: {error}");
            }

            // Must be a GameObject we own; this plugin component is destroyed on
            // frame 0 along with BepInEx_Manager. See ArchipelagoOverlay.
            ArchipelagoOverlay.Install();

            ArchipelagoConsole.LogMessage($"{ModDisplayInfo} loaded!");
        }
        catch (Exception e)
        {
            Logger.LogError($"{ModDisplayInfo} FAILED TO INITIALIZE in Awake(). The mod is now in a " +
                $"partially-loaded state and its GUI/patches may not work.{Environment.NewLine}{e}");
        }
    }



    // This component does NOT draw the overlay - see ArchipelagoOverlay for why.
    // In short: BepInEx's entrypoint (Application..cctor) runs before the first
    // scene loads, and Unity resets the DontDestroyOnLoad scene when it does, so
    // BepInEx_Manager and every plugin component on it are destroyed on frame 0.
    // An OnGUI here would never be called even once. Harmony patches and static
    // state are unaffected, which is why the rest of the mod works regardless.
    //
    // Kept as a one-line breadcrumb: if a future BepInEx or Unity version stops
    // doing this, the absence of this line in the log makes that visible instead
    // of leaving a stale workaround silently in place.
    private void OnDestroy()
    {
        Logger.LogInfo($"[Diag:Host] BepInEx_Manager component destroyed on frame {Time.frameCount} " +
            "(expected in this game; the overlay lives on its own GameObject).");
    }


}