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



[BepInPlugin(PluginGUID, PluginName, PluginVersion)]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGUID = "org.Finnegan_McD.Straftapelago";
    public const string PluginName = "Straftapelago.Finnegan_McD.org";
    public const string PluginVersion = "1.0.0";

    public const string ModDisplayInfo = $"{PluginName} v{PluginVersion}";
    private const string APDisplayInfo = $"Archipelago v{ArchipelagoClient.APVersion}";
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
            try
            {
                ArchipelagoConsole.Awake();
            } catch (Exception e)
            {
                BepinLogger.LogError($"Failed to initialize ArchipelagoConsole: {e}");
            }
            
            new Harmony(PluginGUID).PatchAll();

            ArchipelagoConsole.LogMessage($"{ModDisplayInfo} loaded!");
        }
        catch (Exception e)
        {
            Logger.LogError($"{ModDisplayInfo} FAILED TO INITIALIZE in Awake(). The mod is now in a " +
                $"partially-loaded state and its GUI/patches may not work.{Environment.NewLine}{e}");
        }
    }



    private void OnGUI()
    {
        BepinLogger.LogInfo("mod Gui");
        // show the mod is currently loaded in the corner
        GUI.Label(new Rect(16, 16, 300, 20), ModDisplayInfo);
        ArchipelagoConsole.OnGUI();

        string statusMessage;
        // show the Archipelago Version and whether we're connected or not
        if (ArchipelagoClient.Authenticated)
        {
            // if your game doesn't usually show the cursor this line may be necessary
            // Cursor.visible = false;

            statusMessage = " Status: Connected";
            GUI.Label(new Rect(16, 50, 300, 20), APDisplayInfo + statusMessage);
        }
        else
        {
            // if your game doesn't usually show the cursor this line may be necessary
            // Cursor.visible = true;

            statusMessage = " Status: Disconnected";
            GUI.Label(new Rect(16, 50, 300, 20), APDisplayInfo + statusMessage);
            GUI.Label(new Rect(16, 70, 150, 20), "Host: ");
            GUI.Label(new Rect(16, 90, 150, 20), "Player Name: ");
            GUI.Label(new Rect(16, 110, 150, 20), "Password: ");

            ArchipelagoClient.ServerData.Uri = GUI.TextField(new Rect(150, 70, 150, 20),
                ArchipelagoClient.ServerData.Uri);
            ArchipelagoClient.ServerData.SlotName = GUI.TextField(new Rect(150, 90, 150, 20),
                ArchipelagoClient.ServerData.SlotName);
            ArchipelagoClient.ServerData.Password = GUI.TextField(new Rect(150, 110, 150, 20),
                ArchipelagoClient.ServerData.Password);

            // requires that the player at least puts *something* in the slot name
            if (GUI.Button(new Rect(16, 130, 100, 20), "Connect") &&
                !ArchipelagoClient.ServerData.SlotName.IsNullOrWhiteSpace())
            {
                ArchipelagoClient.Connect();
            }
        }
        // this is a good place to create and add a bunch of debug buttons
    }


}