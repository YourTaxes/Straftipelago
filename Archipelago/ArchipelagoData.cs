using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Straftapelago.Finnegan_McD.org.Utils;

namespace Straftapelago.Finnegan_McD.org.Archipelago;

/// <summary>
/// What the room asks of this slot before it counts as finished - the apworld's WinCondition
/// Choice option, whose numbering these values have to match because slot data carries the
/// number rather than the name.
/// </summary>
public enum GoalCondition
{
    /// <summary>Weapons_Complete alone.</summary>
    WeaponKills = 0,

    /// <summary>Takes_Complete alone.</summary>
    Wins = 1,

    /// <summary>Both events.</summary>
    Both = 2,
}

public class ArchipelagoData
{
    public string Uri;
    public string SlotName;
    public string Password;
    public int Index;

    public List<long> CheckedLocations;

    /// <summary>
    /// The room's seed, for checking that a loaded session belongs to the room it is connecting
    /// to.
    /// </summary>
    private string seed;

    private Dictionary<string, object> slotData;

    public bool NeedSlotData => slotData == null;

    /// <summary>
    /// Whether the room wants this slot linked to the multiworld's deaths. Read out of slot data
    /// on connect, so the room's YAML is the only thing that decides it.
    /// </summary>
    public bool DeathLink { get; private set; }

    /// <summary>
    /// How many of the local player's own deaths it takes to send one death out to the
    /// multiworld - the apworld's DeathsPerLink Range option. Outgoing only: a death arriving
    /// from another world always kills, whatever this is. 1 shares every death.
    /// </summary>
    public int DeathsPerLink { get; private set; } = DefaultDeathsPerLink;

    /// <summary>Whether the room asked for Green Mode. Mirrored onto the Mod Menu setting.</summary>
    public bool GreenMode { get; private set; }

    /// <summary>
    /// The room's New Weapon Chance, as a percentage. Mirrored onto the Mod Menu setting.
    /// </summary>
    public int NewWeaponChance { get; private set; }

    /// <summary>
    /// Whether the propeller, repulsar and the stun weapons start in the roulette. They carry no
    /// checks, so they are unlocked-and-already-killed-with or absent, never locked.
    /// </summary>
    public bool NonDamagingWeapons { get; private set; }

    /// <summary>Whether the Bublee starts in the roulette. Same no-check treatment.</summary>
    public bool UnusedWeapons { get; private set; }

    /// <summary>Whether the flashlight starts in the roulette. Same no-check treatment.</summary>
    public bool UselessWeapons { get; private set; }

    /// <summary>
    /// Which of the room's two goals this slot has to meet to finish - the apworld's
    /// WinCondition Choice option.
    /// </summary>
    public GoalCondition WinCondition { get; private set; } = GoalCondition.Both;

    /// <summary>
    /// How many takes have to be won for the Takes_Complete event, when the goal wants them.
    /// </summary>
    public int WinThreshold { get; private set; } = DefaultWinThreshold;

    /// <summary>
    /// What percentage of the check-carrying weapons have to have earned their first-kill check
    /// for the Weapons_Complete event.
    /// </summary>
    public int WeaponGoalThreshold { get; private set; } = DefaultWeaponGoalThreshold;

    /// <summary>
    /// How many Round_N locations the room has, and therefore the last round win that is worth
    /// sending a check for. A cap, not a goal: the apworld creates Round_1 through Round_N and
    /// no more, so a session that runs past N has nothing left to send.
    /// </summary>
    public int RoundChecks { get; private set; } = DefaultRoundChecks;

    // The slot data keys the room publishes these options under. They are the option attribute
    // names from the apworld's StraftatOptions dataclass, which is what fill_slot_data's
    // options.as_dict() keys the dictionary on.
    private const string DeathLinkKey = "deathlink";
    private const string DeathsPerLinkKey = "deaths_per_link";
    private const string GreenModeKey = "green_mode";
    private const string NewWeaponChanceKey = "new_weapon_chance";
    private const string NonDamagingWeaponsKey = "non_damaging_weapons";
    private const string UnusedWeaponsKey = "unused_weapons";
    private const string UselessWeaponsKey = "useless_weapons";
    private const string WinConditionKey = "win_condition";
    private const string WinThresholdKey = "win_threshold";
    private const string WeaponGoalThresholdKey = "weapon_goal_threshold";
    private const string RoundChecksKey = "round_checks";

    // The apworld's own defaults for these options, used when the room sends none of them - which
    // is what an apworld older than this mod does. Guessing "no goal at all" there would silently
    // hand the player a world that completes on connect.
    private const int DefaultWinThreshold = 5;
    private const int DefaultWeaponGoalThreshold = 50;
    private const int DefaultRoundChecks = 30;
    private const int DefaultDeathsPerLink = 1;

    // Deliberately wider than the apworld's WinThreshold Range (1-100): this one is a GOAL, and
    // clamping a raised threshold down would quietly complete the world early. The upper bound
    // only rejects a garbage value.
    private const int WinThresholdMinimum = 1;
    private const int WinThresholdMaximum = 1000;

    // A percentage genuinely cannot be outside this, so clamping costs nothing.
    private const int WeaponGoalThresholdMinimum = 0;
    private const int WeaponGoalThresholdMaximum = 100;

    // The bounds of the apworld's RoundChecks Range option. Clamped, because this is a count of
    // locations that either exist or do not: a value above the maximum names a Round_N the room
    // cannot have.
    private const int RoundChecksMinimum = 0;
    private const int RoundChecksMaximum = 100;

    // The bounds of the apworld's DeathsPerLink Range option. The floor matters: 0 or below
    // would mean "one link per no deaths", which the counter cannot express, and clamping to 1
    // gives the every-death behaviour a room without the option already has.
    private const int DeathsPerLinkMinimum = 1;
    private const int DeathsPerLinkMaximum = 100;

    // The bounds of the apworld's NewWeaponChance Range option. A value outside them is clamped
    // rather than refused, because the Mod Menu slider this feeds is bound to the same range and
    // would not be able to display it.
    private const int NewWeaponChanceMinimum = 1;
    private const int NewWeaponChanceMaximum = 100;

    public ArchipelagoData()
    {
        Uri = "localhost";
        SlotName = "Player1";
        CheckedLocations = new();
    }

    public ArchipelagoData(string uri, string slotName, string password)
    {
        Uri = uri;
        SlotName = slotName;
        Password = password;
        CheckedLocations = new();
    }

    /// <summary>
    /// Reads the room's answer to this slot's YAML out of slot data. A key the room does not
    /// send leaves the setting where it already was, which is why the two settings with a local
    /// Mod Menu equivalent read their fallback out of it. Nothing here touches Unity - this runs
    /// on the ThreadPool thread HandleConnectResult is on, and putting these into effect is
    /// <see cref="ArchipelagoClient"/>'s job.
    /// </summary>
    /// <param name="roomSlotData">This slot's slot data, as the room sent it.</param>
    /// <param name="roomSeed">Seed name of this session.</param>
    public void SetupSession(Dictionary<string, object> roomSlotData, string roomSeed)
    {
        // Kept, not overwritten, when the room sends nothing: a reconnect asks for slot data
        // only when there is none, so the second login legitimately answers null. Every reader
        // below then falls back to its current value.
        if (roomSlotData != null) slotData = roomSlotData;
        seed = roomSeed;

        DeathLink = ReadToggle(slotData, DeathLinkKey, DeathLink);
        DeathsPerLink = ReadRange(slotData, DeathsPerLinkKey, DeathsPerLink,
            DeathsPerLinkMinimum, DeathsPerLinkMaximum);
        GreenMode = ReadToggle(slotData, GreenModeKey, ArchipelagoMenu.GreenMode?.Value ?? false);
        NewWeaponChance = ReadRange(slotData, NewWeaponChanceKey,
            ArchipelagoMenu.NewWeaponChance?.Value ?? NewWeaponChanceMaximum / 2,
            NewWeaponChanceMinimum, NewWeaponChanceMaximum);

        NonDamagingWeapons = ReadToggle(slotData, NonDamagingWeaponsKey, NonDamagingWeapons);
        UnusedWeapons = ReadToggle(slotData, UnusedWeaponsKey, UnusedWeapons);
        UselessWeapons = ReadToggle(slotData, UselessWeaponsKey, UselessWeapons);

        WinCondition = (GoalCondition)ReadRange(slotData, WinConditionKey, (int)WinCondition,
            (int)GoalCondition.WeaponKills, (int)GoalCondition.Both);
        WinThreshold = ReadRange(slotData, WinThresholdKey, WinThreshold,
            WinThresholdMinimum, WinThresholdMaximum);
        WeaponGoalThreshold = ReadRange(slotData, WeaponGoalThresholdKey, WeaponGoalThreshold,
            WeaponGoalThresholdMinimum, WeaponGoalThresholdMaximum);
        RoundChecks = ReadRange(slotData, RoundChecksKey, RoundChecks,
            RoundChecksMinimum, RoundChecksMaximum);
    }

    /// <summary>
    /// Pulls one Toggle option out of slot data. An Archipelago Toggle is a 0/1 on the wire and
    /// slot data deserializes into object, so this arrives as a long far more often than as a
    /// bool; Convert.ToBoolean covers that, a real bool and a string alike, and anything it
    /// cannot read is reported and left at <paramref name="current"/>.
    /// </summary>
    /// <param name="current">What the setting is now, and what it stays as if the room is silent.</param>
    private static bool ReadToggle(Dictionary<string, object> roomSlotData, string key, bool current)
    {
        if (!TryGetSetting(roomSlotData, key, current, out object value)) return current;

        try
        {
            return Convert.ToBoolean(value);
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError(
                $"Could not read '{key}' from slot data (got '{value}'); leaving it at " +
                $"{current}.{Environment.NewLine}{e}");
            return current;
        }
    }

    /// <summary>
    /// Pulls one Range option out of slot data and clamps it into the range the game can use.
    /// </summary>
    /// <param name="current">What the setting is now, and what it stays as if the room is silent.</param>
    private static int ReadRange(
        Dictionary<string, object> roomSlotData, string key, int current, int minimum, int maximum)
    {
        if (!TryGetSetting(roomSlotData, key, current, out object value)) return current;

        int parsed;
        try
        {
            parsed = Convert.ToInt32(value);
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError(
                $"Could not read '{key}' from slot data (got '{value}'); leaving it at " +
                $"{current}.{Environment.NewLine}{e}");
            return current;
        }

        if (parsed >= minimum && parsed <= maximum) return parsed;

        // Clamped rather than refused: the room and this mod are two versions of the same
        // option, and a range that has since widened on the apworld side should not cost the
        // player the setting entirely.
        int clamped = parsed < minimum ? minimum : maximum;
        Plugin.BepinLogger.LogWarning(
            $"Slot data's '{key}' is {parsed}, outside the {minimum}-{maximum} this mod accepts; " +
            $"using {clamped}.");
        return clamped;
    }

    /// <summary>
    /// The lookup both readers share, including the warning for a key the room never sent.
    /// </summary>
    /// <returns>False when there is nothing to read, in which case the caller keeps its value.</returns>
    private static bool TryGetSetting(
        Dictionary<string, object> roomSlotData, string key, object current, out object value)
    {
        if (roomSlotData != null && roomSlotData.TryGetValue(key, out value) && value != null) return true;

        // Logged rather than passed over: an apworld older than this mod simply will not send
        // some of these, and "the room never changed it" is a lot easier to understand with
        // this line in LogOutput.log than without it.
        Plugin.BepinLogger.LogWarning(
            $"Slot data has no '{key}' entry, so it stays at {current}. The room's apworld does " +
            "not offer that option.");

        value = null;
        return false;
    }

    /// <summary>
    /// One line per slot data entry, for printing into the Archipelago console once the login
    /// that carried it has succeeded. Values go back through Newtonsoft rather than ToString,
    /// because a list arrives as a JArray and a nested table as a JObject, both of which would
    /// otherwise print as their type name.
    /// </summary>
    public IEnumerable<string> DescribeSlotData()
    {
        if (slotData == null || slotData.Count == 0)
        {
            yield return "Slot data: the room sent none.";
            yield break;
        }

        yield return $"Slot data ({slotData.Count} entries):";

        // Ordered so the same room prints the same list every connect; the dictionary comes off
        // the wire in whatever order the server serialized it.
        foreach (var entry in slotData.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            yield return $"    {entry.Key}: {DescribeValue(entry.Value)}";
    }

    /// <summary>Renders a single slot data value for <see cref="DescribeSlotData"/>.</summary>
    private static string DescribeValue(object value)
    {
        if (value == null) return "null";

        try
        {
            return JsonConvert.SerializeObject(value);
        }
        catch (Exception e)
        {
            // Printing slot data is diagnostic, so one unserializable value must not take the
            // rest of the list - or the connect message it is printed after - down with it.
            Plugin.BepinLogger.LogError($"Could not render a slot data value.{Environment.NewLine}{e}");
            return value.ToString();
        }
    }

    /// <summary>The whole object as a json string, for writing to a file.</summary>
    public override string ToString()
    {
        return JsonConvert.SerializeObject(this);
    }
}