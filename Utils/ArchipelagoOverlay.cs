using System.Collections.Generic;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Archipelago;
using Straftapelago.Finnegan_McD.org.Patches;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// Hosts the Archipelago overlay's IMGUI on a GameObject this mod owns, rather
/// than on BepInEx's manager object.
/// </summary>
/// <remarks>
/// BepInEx's configured entrypoint here is <c>UnityEngine.CoreModule</c> /
/// <c>Application</c> / <c>.cctor</c>, which runs before the first scene has
/// loaded. Unity resets the DontDestroyOnLoad scene when that first scene comes
/// up, so everything created that early is destroyed with it - including
/// BepInEx_Manager, and therefore every plugin component BepInEx hosts on it.
/// Diagnostics on this install caught it precisely: the Plugin component logged
/// OnEnable, then OnDisable and OnDestroy, all on frame 0, so its OnGUI never
/// ran once and the overlay never drew. (UnityExplorer was unaffected in the
/// same run only because UniverseLib defers creating its objects until after the
/// game is up, which is the same workaround this class applies.)
///
/// Harmony patches and static state are unaffected by any of this - the assembly
/// stays loaded - which is why the rest of the mod worked while the GUI did not,
/// and why re-creating the host object from a static hook works.
/// </remarks>
internal class ArchipelagoOverlay : MonoBehaviour
{
    private const string HostName = "Straftapelago_Overlay";

    // Shared by both panels, and all fractions of the screen rather than pixels so the
    // overlay scales with resolution instead of assuming one - the same thing
    // ArchipelagoConsole does. The two panels are stacked: the progress block sits
    // directly above WeaponPanelTopFraction, which is where the weapon list starts.
    private const float PanelLeftFraction = 0.02f;
    private const float WeaponPanelTopFraction = 0.22f;
    private const float PanelPaddingFraction = 0.006f;
    private const float EntryHeightFraction = 0.022f;
    private const float EntryFontFraction = 0.016f;

    /// <summary>Unity fake-null once destroyed, which is what drives re-creation.</summary>
    private static ArchipelagoOverlay instance;

    private static int spawnCount;
    private static bool warnedAboutChurn;

    /// <summary>
    /// Guards against burning a spawn every scene load if something in the game
    /// actively destroys the host. Normal operation is exactly two spawns: one
    /// from <see cref="Install"/> that dies with the manager object on frame 0,
    /// and one from the first scene load, which then persists for the session.
    /// </summary>
    private const int MaxSpawns = 8;

    private bool loggedFirstOnGui;

    public static void Install()
    {
        // Static handler, so it survives this component being destroyed.
        SceneManager.sceneLoaded += OnSceneLoaded;
        Spawn("plugin Awake");
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (instance != null) return;
        Spawn($"scene load '{scene.name}'");
    }

    private static void Spawn(string reason)
    {
        if (spawnCount >= MaxSpawns)
        {
            if (warnedAboutChurn) return;
            warnedAboutChurn = true;
            Plugin.BepinLogger.LogWarning(
                $"[Overlay] host destroyed {MaxSpawns} times; giving up re-creating it. " +
                "Something is actively tearing down this mod's GameObject, which is not " +
                "the frame-0 DontDestroyOnLoad reset this is meant to work around.");
            return;
        }

        spawnCount++;
        GameObject host = new GameObject(HostName);
        DontDestroyOnLoad(host);
        instance = host.AddComponent<ArchipelagoOverlay>();

        Plugin.BepinLogger.LogInfo(
            $"[Overlay] host created (#{spawnCount}) after {reason}, frame={Time.frameCount}");
    }

    private void OnDestroy()
    {
        // Expected exactly once, on frame 0, for the pre-first-scene instance.
        // Anything later means the host is being torn down for another reason.
        Plugin.BepinLogger.LogInfo($"[Overlay] host destroyed, frame={Time.frameCount} drewGui={loggedFirstOnGui}");
    }

    /// <summary>
    /// Pumps the two message queues and the action queue. This is the mod's one guaranteed
    /// per-frame main-thread callback, and all three sinks need exactly that: their contents
    /// are produced on the Archipelago client's websocket and ThreadPool threads, where no
    /// Unity API may be touched.
    /// </summary>
    private void Update()
    {
        ArchipelagoConsole.Pump();
        Killfeed.Pump();

        // Last of the three. An action here can write to either of the sinks above - applying
        // Green Mode puts a line in the killfeed - and draining it after them means such a line
        // waits a frame rather than sitting in a queue that has already been pumped.
        MainThreadActions.Pump();
    }

    private void OnGUI()
    {
        if (!loggedFirstOnGui)
        {
            loggedFirstOnGui = true;
            Plugin.BepinLogger.LogInfo($"[Overlay] drawing, frame={Time.frameCount}");
        }

        // The pause gate for both panels, here rather than in each of them: they are one
        // stacked block as far as the player is concerned, and a gate that let one through
        // without the other would leave a stat box floating over an empty screen.
        //
        // Gated on pause so neither ever sits on top of gameplay, and hidden again once the
        // player opens Settings from the pause menu: that screen is a full-width layout they
        // would sit on top of, and pause stays true the whole time it is up. See
        // InSettingsMenu. PauseManager.Instance is null-checked rather than assumed:
        // HUDTween's own null-dereference of that singleton is one of the open crash
        // candidates, so it is demonstrably not always there.
        if (PauseManager.Instance == null || !PauseManager.Instance.pause) return;
        if (InSettingsMenu()) return;

        // The connection UI that used to be here - the Archipelago version/status labels, the
        // host/slot/password text fields, the Connect button and the console window - is now
        // the mod's Mod Menu page (ArchipelagoMenu) and the killfeed (ArchipelagoConsole).
        // What is left is this mod's own unlocked-weapons panel, which was never part of that
        // default Archipelago GUI.
        DrawSessionProgress();
        DrawObtainedWeapons();
    }

    /// <summary>
    /// The two numbers that say how the run is going, in a block directly above the weapon
    /// list: takes won, and how much of the weapon roster has actually been earned.
    /// </summary>
    /// <remarks>
    /// <para>The three lines have deliberately different spans, which is why each says its own.
    /// Takes and rounds won are this session only - vanilla accumulates neither across matches,
    /// so TakeTracker counts them from process start. Weapons earned is the seed's progress and
    /// survives restarts, because it is rebuilt on connect from the locations the room says this
    /// slot has already checked.</para>
    /// <para>Earned means a first kill was scored with it and its check went out - the ticked
    /// entries in the list below - not merely that the room granted it. The weapons that carry
    /// no check are out of both halves of the fraction; see RouletteState.EarnedWeaponCount.</para>
    /// <para>The first two lines are the room's two goals, so each carries the room's threshold
    /// and takes a tick on the end once <see cref="GoalTracker"/> says that goal is achieved.
    /// Both only
    /// appear while connected: offline no room is asking for anything, and the apworld defaults
    /// ServerData is holding are not a goal anyone agreed to. Rounds won has no goal behind it,
    /// so it never takes a tick - the number beside it is the room's round_checks, which is how
    /// many Round_N checks exist, not something to reach.</para>
    /// </remarks>
    private static void DrawSessionProgress()
    {
        float panelLeft = Screen.width * PanelLeftFraction;
        float panelPadding = Screen.width * PanelPaddingFraction;
        float entryHeight = Screen.height * EntryHeightFraction;
        float headerHeight = entryHeight * 1.5f;

        bool showGoals = ArchipelagoClient.Authenticated;
        ArchipelagoData serverData = ArchipelagoClient.ServerData;

        var lines = new List<(bool Achieved, string Text)>
        {
            (showGoals && GoalTracker.TakesGoalMet,
                $"Takes won this session: {TakeTracker.TakesWon}"
                + (showGoals ? $" (goal {serverData.WinThreshold})" : "")),
        };

        RouletteState roulette = Plugin.RouletteState;
        int earned = roulette?.EarnedWeaponCount ?? 0;
        int checkable = roulette?.CheckableWeaponCount ?? 0;

        // Zero before the first match: the pool is built off SpawnerManager, which has no
        // weapons until a player object exists, so there is genuinely no roster to be a fraction
        // of yet. Said rather than shown as 0% of 0, which would read as progress having been
        // lost.
        lines.Add((showGoals && GoalTracker.WeaponsGoalMet, checkable > 0
            // Floored, not rounded: 100% has to mean every check is in, and rounding would show
            // it one weapon early on any roster of 67 or more. GoalTracker compares the same two
            // numbers the same way, so the tick can never disagree with the percentage beside it.
            ? $"Weapons earned: {Mathf.FloorToInt(earned * 100f / checkable)}% ({earned}/{checkable})"
              + (showGoals ? $", goal {serverData.WeaponGoalThreshold}%" : "")
            : "Weapons earned: waiting for the first match"));

        // Against the room's cap rather than bare, because that cap is the number that matters to
        // the player: Round_1 through Round_N are checks, and a round won past N sends nothing.
        // Offline the cap is only the apworld's default sitting in ServerData, which no room has
        // agreed to, so the plain count is shown instead - the same reason the two goal lines drop
        // their thresholds when showGoals is false.
        lines.Add((false, showGoals
            ? $"Rounds won this session: {TakeTracker.RoundsWon} / {serverData.RoundChecks} checks"
            : $"Rounds won this session: {TakeTracker.RoundsWon}"));

        // Packed to the width the longest line needs, with room for the tick that a met goal
        // adds to the end of it, and capped to the same left-half budget the weapon list keeps
        // to so it can never cover the pause menu either.
        float entryWidth = Mathf.Min(Screen.width * 0.21f,
            Screen.width * 0.5f - panelLeft - panelPadding * 2f);
        float panelWidth = entryWidth + panelPadding * 2f;
        float panelHeight = headerHeight + lines.Count * entryHeight + entryHeight * 0.5f;

        // Bottom-anchored to the weapon list rather than given a top of its own, so the gap
        // between the two blocks stays put whatever this one ends up containing.
        float panelTop = Screen.height * WeaponPanelTopFraction - panelHeight - entryHeight * 0.5f;

        GUI.Box(new Rect(panelLeft, panelTop, panelWidth, panelHeight), "");

        GUIStyle style = EntryStyle();
        float entryLeft = panelLeft + panelPadding;

        GUI.Label(new Rect(entryLeft, panelTop + entryHeight * 0.25f, entryWidth, headerHeight),
            "Progress", style);

        for (int i = 0; i < lines.Count; i++)
        {
            // U+2713 on the end of the line, the same mark the weapon list puts after a weapon
            // that has earned its check.
            string text = lines[i].Achieved ? $"{lines[i].Text} ✓" : lines[i].Text;

            GUI.Label(new Rect(entryLeft, panelTop + headerHeight + i * entryHeight, entryWidth, entryHeight),
                text, style);
        }
    }

    /// <summary>The one label style both panels draw with, so they cannot drift apart.</summary>
    private static GUIStyle EntryStyle() =>
        new GUIStyle(GUI.skin.label)
        {
            fontSize = (int)(Screen.height * EntryFontFraction),
            normal = { textColor = Color.white },
        };

    /// <summary>
    /// The local player's unlocked weapons. Drawn under the progress block, and like it only
    /// while the game is paused - see <see cref="OnGUI"/> for that gate.
    /// </summary>
    /// <remarks>
    /// RouletteState holds this machine's player's pool and nothing else — the roll happens
    /// locally and only the chosen prefab is ever sent — so this needs no networking and is
    /// already the right list for whoever is looking at the screen.
    ///
    /// Confined to the left half of the screen so it cannot cover the pause menu.
    /// </remarks>
    private static void DrawObtainedWeapons()
    {
        RouletteState roulette = Plugin.RouletteState;
        if (roulette == null) return;

        // One list, in progress order: weapons still waiting for their first kill at the
        // top, the ones that already earned their check at the bottom with a tick. Showing
        // both is what stops a weapon appearing to vanish from the panel the moment it is
        // used - it has not been lost, it has been completed.
        var obtained = new List<GameObject>(roulette.obtained_Items);
        int firstKillEarned = obtained.Count;
        obtained.AddRange(roulette.hasKill_Items);

        float panelLeft = Screen.width * PanelLeftFraction;
        float panelTop = Screen.height * WeaponPanelTopFraction;
        float panelPadding = Screen.width * PanelPaddingFraction;
        float entryHeight = Screen.height * EntryHeightFraction;
        float headerHeight = entryHeight * 1.5f;

        // A weapon name is much narrower than the old 22%-of-screen column, so the columns are
        // packed to the width the text actually needs plus a gap. That is what buys the third
        // column inside the same left-half budget.
        float entryWidth = Screen.width * 0.145f;
        float columnGap = Screen.width * 0.008f;
        float columnStride = entryWidth + columnGap;

        // Still confined to the left half of the screen so it cannot cover the pause menu, so
        // the number of columns is however many fit in that budget rather than however many
        // the list would like.
        float maxPanelWidth = Screen.width * 0.5f - panelLeft;
        float maxColumnsWidth = maxPanelWidth - panelPadding * 2f + columnGap;
        int entriesPerColumn = Mathf.Max(1, (int)((Screen.height * 0.68f - headerHeight) / entryHeight));
        int maxColumns = Mathf.Max(1, (int)(maxColumnsWidth / columnStride));

        // Hard cap: with 71 weapons in the game the full list can outgrow even the columns,
        // and a panel that runs off the screen is worse than one that says how much it is
        // hiding. The "... and N more" line costs an entry, so it comes out of the capacity.
        int capacity = entriesPerColumn * maxColumns;
        bool truncated = obtained.Count > capacity;
        int weaponCount = truncated ? capacity - 1 : obtained.Count;
        int entryCount = weaponCount + (truncated ? 1 : 0);

        int columnCount = Mathf.Max(1, Mathf.CeilToInt(entryCount / (float)entriesPerColumn));
        int rowCount = Mathf.Min(Mathf.Max(entryCount, 1), entriesPerColumn);

        float panelWidth = columnCount * columnStride - columnGap + panelPadding * 2f;
        float panelHeight = headerHeight + rowCount * entryHeight + entryHeight * 0.5f;

        GUI.Box(new Rect(panelLeft, panelTop, panelWidth, panelHeight), "");

        GUIStyle style = EntryStyle();

        float firstColumnLeft = panelLeft + panelPadding;
        float firstEntryTop = panelTop + headerHeight;

        GUI.Label(new Rect(firstColumnLeft, panelTop + entryHeight * 0.25f,
            panelWidth - panelPadding * 2f, headerHeight),
            // No "✓ = kill earned" legend: the header Rect is only as wide as the packed
            // columns, so with a single column that text clips rather than explains.
            $"Unlocked weapons ({obtained.Count})", style);

        for (int i = 0; i < entryCount; i++)
        {
            // Fill each column top to bottom before starting the next one, so the numbering
            // reads down a column the way the single-column list used to.
            float entryLeft = firstColumnLeft + i / entriesPerColumn * columnStride;
            float entryTop = firstEntryTop + i % entriesPerColumn * entryHeight;

            string text;
            if (truncated && i == entryCount - 1)
            {
                text = $"... and {obtained.Count - weaponCount} more";
            }
            else
            {
                GameObject weapon = obtained[i];

                // U+2713. If a future Unity build's default GUI font does not carry it the
                // entry shows a box, in which case swap this for a plain "*".
                string killMark = i >= firstKillEarned ? " ✓" : "";

                // DisplayNameOf, not weapon.name: the pool is keyed on prefab names, and
                // several of those are nothing like what the game calls the weapon on screen -
                // the prefab named "Nugget" is the serac, "AK-K" is the ak. This panel is read
                // next to the game, so it spells them the way the game does. DisplayNameOf
                // falls back to the prefab name for anything carrying no ItemBehaviour.
                text = $"{i + 1}. {(weapon == null ? "<missing>" : RouletteState.DisplayNameOf(weapon))}{killMark}";
            }

            GUI.Label(new Rect(entryLeft, entryTop, entryWidth, entryHeight), text, style);
        }
    }
    /// <summary>Cached so the reflection below does not run on every OnGUI pass.</summary>
    private static PauseManager settingsMenuOwner;
    private static GameObject settingsMenuObject;
    private static bool warnedAboutMissingSettingsMenu;

    /// <summary>
    /// True while the Settings screen is up.
    /// </summary>
    /// <remarks>
    /// <para>PauseManager.pause stays true the whole time Settings is open — it means "the
    /// game is paused", not "the pause menu is the thing on screen" — so it cannot tell the
    /// two apart on its own. The private <c>optionsMenu</c> GameObject can: vanilla
    /// <c>PauseManager.Menu()</c> (the Escape handler) reads its <c>activeSelf</c> and toggles
    /// it, which is exactly the state being asked about here.</para>
    /// <para>This also covers Mod Menu, including this mod's own page, without a second check:
    /// its <c>PauseManagerPatch.InitModsTab</c> postfix adds a "ModsTab" next to the vanilla
    /// PcTab/AudioTab/GraphTab under the same OPTIONS HUD, so the mod list is a tab inside
    /// this screen rather than a separate one.</para>
    /// <para><c>activeInHierarchy</c> rather than <c>activeSelf</c>, which is what vanilla
    /// happens to use: the question here is "is it on screen", and a parent being hidden would
    /// leave activeSelf true while nothing is actually visible.</para>
    /// </remarks>
    private static bool InSettingsMenu()
    {
        PauseManager pauseManager = PauseManager.Instance;
        if (pauseManager == null) return false;

        // Re-resolve when the singleton is replaced; Unity's fake-null makes this also fire
        // when the previous one was destroyed.
        if (pauseManager != settingsMenuOwner)
        {
            settingsMenuOwner = pauseManager;
            settingsMenuObject = Traverse.Create(pauseManager).Field("optionsMenu").GetValue<GameObject>();

            if (settingsMenuObject == null && !warnedAboutMissingSettingsMenu)
            {
                // Once, not every frame. Failing open (list stays visible) is the harmless
                // direction, but a silent failure would look like the gate simply not working.
                warnedAboutMissingSettingsMenu = true;
                Plugin.BepinLogger.LogWarning(
                    "[Overlay] PauseManager.optionsMenu did not resolve, so the weapon list " +
                    "cannot tell the pause menu from the Settings screen and will stay visible " +
                    "on both. The field was probably renamed by a game update.");
            }
        }

        return settingsMenuObject != null && settingsMenuObject.activeInHierarchy;
    }
}
