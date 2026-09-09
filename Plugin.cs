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



// All three dependencies are hard, because the mod cannot work without any of them: Mycelium
// carries the roulette roll to the host, Mod Menu hosts the only login UI, and ChatCommands is
// the Archipelago console itself. Declaring them also fixes load order, since BepInEx runs a
// dependency's Awake before this one - so ChatCommands' registry exists by the time this mod
// adds commands to it.
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

    // The mod's display name, and only that: BepInPlugin hands it to Mod Menu, which shows it on
    // the mod list and on this mod's tab. The Mod Menu API can override the icon and the
    // description but not this.
    public const string PluginName = "Straftipelago";

    public const string PluginVersion = "1.0.0";

    public const string ModDisplayInfo = $"{PluginName} v{PluginVersion}";
    public static ManualLogSource BepinLogger;
    public static ArchipelagoClient ArchipelagoClient;
    public static GameObject RouletteItemPrefab;

    // The local player's roulette pools, created once in Awake and never replaced: the roulette
    // patches read it for the roll and the pickup rules, kill detection for the first-kill
    // checks. A plain C# object rather than a MonoBehaviour, since it has no per-frame work and
    // a static reference keeps it alive across scene changes on its own.
    public static RouletteState RouletteState;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private const int STD_INPUT_HANDLE = -10;
    private const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    private const uint ENABLE_EXTENDED_FLAGS = 0x0080;

    // Windows consoles default to QuickEdit Mode, which suspends the process's writes to the
    // console the moment the window is clicked into a selection - and since Unity's game loop is
    // the thread doing the logging, that freezes the whole game until the selection is
    // cancelled. Clearing the flag prevents that for the lifetime of the console window.
    // ENABLE_EXTENDED_FLAGS has to be set for the QuickEdit bit to take effect at all. Nothing
    // calls this at the moment; Awake is where it belongs when it is wanted.
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
        // BepInEx only surfaces a failed Awake as a terse chainloader line, and a partly
        // initialized plugin then fails in confusing ways later, so the whole thing is caught
        // and logged here. Through the inherited `Logger` rather than the static BepinLogger
        // field, so a throw before that field is assigned still gets reported.
        try
        {
            BepinLogger = Logger;
            BepinLogger.LogInfo("Straftapelago plugin loading.");

            // First thing after the logger: this binds the config, and every later step here -
            // and every patch - may read an entry. Once only, as Mod Menu's
            // RegisterContentBuilder throws on a second call from this assembly.
            try
            {
                ArchipelagoMenu.Install(Config);
            }
            catch (Exception e)
            {
                BepinLogger.LogError($"Failed to register the Mod Menu page: {e}");
            }

            // After the config, which Roll reads, and before PatchAll, so no patch can observe
            // this as null. The constructor only allocates the lists: the weapons are filled in
            // on the first PlayerPickup.Awake, because SpawnerManager is not up this early.
            try
            {
                RouletteState = new RouletteState();
            }
            catch (Exception e)
            {
                BepinLogger.LogError($"Failed to create the roulette state: {e}");
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

            // Its own try/catch and its own registration, on the same Mycelium mod id, so losing
            // one of the two sets of RPCs does not cost the other.
            try
            {
                MadeInHeavenNet.Install();
            }
            catch (Exception e)
            {
                BepinLogger.LogError($"Failed to register Made in Heaven RPCs with Mycelium: {e}");
            }

            // Before PatchAll, so the scene-load handler that clears its snapshot is
            // subscribed by the time the patch it feeds can fire.
            try
            {
                TakeTracker.Install();
            }
            catch (Exception e)
            {
                BepinLogger.LogError($"Failed to install the take counter: {e}");
            }

            Harmony harmony = new Harmony(PluginGUID);
            harmony.PatchAll();

            // Separate from PatchAll, and guarded, because these targets are discovered by
            // searching the game's IL rather than named in an attribute: one that fails to
            // patch must not take the rest of the mod's initialization with it.
            try
            {
                SuicideScopes.Install(harmony);
            }
            catch (Exception error)
            {
                BepinLogger.LogError($"Failed to install suicide-detection scopes: {error}");
            }

            // Must be a GameObject the mod owns; this plugin component is destroyed on
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



    // Logs when the game destroys the BepInEx_Manager this component lives on.
    private void OnDestroy()
    {
        Logger.LogInfo($"[Diag:Host] BepInEx_Manager component destroyed on frame {Time.frameCount} " +
            "(expected in this game; the overlay lives on its own GameObject).");
    }


}