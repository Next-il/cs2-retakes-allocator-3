using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using PanoramaManager;
using Microsoft.Extensions.Logging;
using RetakesAllocatorCore;
using RetakesAllocatorCore.Db;

namespace RetakesAllocator.AdvancedMenus;

/// <summary>
/// The <c>!guns</c> screen as a Panorama grid instead of a chat menu.
///
/// <para>Reads nothing of its own: the categories come from
/// <see cref="WeaponHelpers.GetPossibleWeaponsForAllocationType"/>, which is already filtered by the
/// config's <c>UsableWeapons</c>, and saving goes through <see cref="OnWeaponCommandHelper"/> - the
/// same path the chat command uses. So the allocator's config, validation and database are untouched
/// and this is only a different way to press the same buttons.</para>
///
/// <para><b>Selections are not saved as you click.</b> They are held per player until Save, so Close
/// discards. That is what makes Save a real button.</para>
/// </summary>
public sealed class WeaponHudMenu
{
    private const string Layout = "panorama/layout/custom_game/weapon_hud.vxml_c";

    /// <summary>Matches the pool in <c>weapon_hud.xml</c>. Ten categories, ten options each, laid
    /// out five per line. Changing these means changing the layout and shipping a new VPK.</summary>
    private const int Groups = 10;   // 5 categories x 2 teams
    private const int Cols   = 12;
    private const int PerRow = 4;

    /// <summary>
    /// The ten category slots, in display order. Five allocation types across both teams - which is
    /// exactly the pool the layout provides, so every category has a home and none is dropped.
    /// A category the config leaves empty collapses out of the layout entirely.
    /// </summary>
    /// <summary>
    /// The ten category slots, in display order. Groups 0-4 fill the T column, 5-9 the CT column;
    /// the layout hard-codes that split, so the order is not cosmetic.
    ///
    /// <para>The RoundType is load-bearing, not decoration. A pistol belongs to BOTH PistolRound and
    /// Secondary, and <c>GetWeaponAllocationTypeForWeaponAndRound</c> disambiguates them using the
    /// round type - passing null makes it return whichever of the two comes first out of an unordered
    /// set. Both pistol categories then save to the same slot, silently overwrite each other, and
    /// reopening finds nothing under the other one. Unambiguous categories ignore this.</para>
    /// </summary>
    private static readonly (string Label, WeaponAllocationType Type, CsTeam Team, RoundType? Round)[] Categories =
    [
        ("Rifles",       WeaponAllocationType.FullBuyPrimary, CsTeam.Terrorist,        RoundType.FullBuy),
        ("Mid-Range",    WeaponAllocationType.HalfBuyPrimary, CsTeam.Terrorist,        RoundType.HalfBuy),
        ("Pistol Round", WeaponAllocationType.PistolRound,    CsTeam.Terrorist,        RoundType.Pistol),
        ("Secondary",    WeaponAllocationType.Secondary,      CsTeam.Terrorist,        RoundType.FullBuy),
        ("Sniper",       WeaponAllocationType.Preferred,      CsTeam.Terrorist,        null),

        ("Rifles",       WeaponAllocationType.FullBuyPrimary, CsTeam.CounterTerrorist, RoundType.FullBuy),
        ("Mid-Range",    WeaponAllocationType.HalfBuyPrimary, CsTeam.CounterTerrorist, RoundType.HalfBuy),
        ("Pistol Round", WeaponAllocationType.PistolRound,    CsTeam.CounterTerrorist, RoundType.Pistol),
        ("Secondary",    WeaponAllocationType.Secondary,      CsTeam.CounterTerrorist, RoundType.FullBuy),
        ("Sniper",       WeaponAllocationType.Preferred,      CsTeam.CounterTerrorist, null),
    ];

    private sealed class Draft
    {
        /// <summary>Which weapons ended up on which group row, so a click on w3_2 can be resolved
        /// back to a weapon without trusting anything the client sent beyond the id.</summary>
        public readonly List<List<CsItem>> Options = [];

        /// <summary>Group index -> chosen weapon. Uncommitted until Save.</summary>
        public readonly Dictionary<int, CsItem> Chosen = new();

        /// <summary>Group index -> what was already saved, so Save only writes real changes.</summary>
        public readonly Dictionary<int, CsItem> Original = new();

        /// <summary>Tile id -> the icon class currently on it, so it can be cleared before the next
        /// one is applied. A tile with two icon classes draws whichever the stylesheet lists last.</summary>
        public readonly Dictionary<string, string> Icons = new();

        /// <summary>Set while a save is in flight. The client decides how often it sends a click, so
        /// without this, holding Save queues one set of database writes per frame against the same
        /// rows.</summary>
        public bool Saving;
    }

    private readonly PanelHandle _menu;
    private readonly ILogger    _logger;
    private readonly Dictionary<ulong, Draft> _drafts = new();

    public WeaponHudMenu(ILogger logger)
    {
        _logger = logger;

        _menu = Panorama.Spawn(Layout, new LayoutContract
        {
            RootPanelId   = "PanoramaRoot",
            RevealClass   = "show",       // the layout animates in rather than collapsing
            CloseButtonId = "wsel_close",
            RowCount      = 1,            // no row pool here; the grid is driven directly

            // Nothing. The layout's z-index puts it above the crosshair without the server touching
            // anyone's HUD, and the radar flag never had a visible effect anyway.
            HideHud       = HideHudFlags.None,
        });

        _menu.OnEvent += OnEvent;
    }

    public void Dispose() => _menu.Dispose();

    /// <summary>
    /// Opens the grid for a player.
    ///
    /// <para><b>The database read runs off the game thread</b>, and the drawing comes back onto it.
    /// Loading preferences is a SQLite query; doing it inline stalls the server for everyone, and
    /// continuing after the await would leave every subsequent native call on a thread-pool thread -
    /// those are not thread-safe, and that corrupts state rather than failing cleanly.</para>
    /// </summary>
    public void Open(CCSPlayerController player)
    {
        if (player is not { IsValid: true })
            return;

        var steamId = player.SteamID;

        Task.Run(async () =>
        {
            UserSetting? settings = null;

            try
            {
                settings = await Queries.GetUserSettings(steamId);
            }
            catch (Exception e)
            {
                // A preference read failing is not a reason to deny the menu - it opens with
                // nothing selected, which is recoverable, rather than not opening at all.
                _logger.LogWarning(e, "[WeaponHud] could not load preferences for {SteamId}", steamId);
            }

            var draft = BuildDraft(settings);

            Server.NextFrame(() =>
            {
                draft.Saving = false;

                if (player is not { IsValid: true })
                    return;

                // Drop drafts belonging to players who have since left. Close clears the normal
                // case, but a disconnect with the menu open never reaches it, so without this the
                // dictionary grows by one entry per such disconnect for the life of the process.
                PruneDrafts();

                _drafts[steamId] = draft;

                _menu.Title    = "Select your weapons";
                _menu.Subtitle = "Retakes";
                _menu.Open(player);

                Render(player, draft);
                _menu.SetVariableFor(player, "team_t", "Terrorists");
                _menu.SetVariableFor(player, "team_ct", "Counter-Terrorists");
                _menu.SetVariableFor(player, "menu_footer", "Click to choose - Save to keep");
            });
        });
    }

    /// <summary>Reads the config and the player's saved preferences into a draft. Pure - no engine
    /// calls - so it is safe to run off the game thread.</summary>
    private Draft BuildDraft(UserSetting? settings)
    {
        var draft = new Draft();

        for (var g = 0; g < Groups; g++)
        {
            var (_, type, team, _) = Categories[g];

            var options = WeaponHelpers
                .GetPossibleWeaponsForAllocationType(type, team)
                .Take(Cols)
                .ToList();

            draft.Options.Add(options);

            if (settings?.GetWeaponPreference(team, type) is { } saved && options.Contains(saved))
            {
                draft.Chosen[g]   = saved;
                draft.Original[g] = saved;
            }
        }

        return draft;
    }

    private void PruneDrafts()
    {
        if (_drafts.Count == 0)
            return;

        var connected = Utilities.GetPlayers()
            .Where(p => p is { IsValid: true })
            .Select(p => p.SteamID)
            .ToHashSet();

        foreach (var gone in _drafts.Keys.Where(id => !connected.Contains(id)).ToList())
            _drafts.Remove(gone);
    }

    private void Render(CCSPlayerController player, Draft draft)
    {
        for (var g = 0; g < Groups; g++)
        {
            var options = draft.Options[g];

            // An empty category collapses out entirely rather than leaving a labelled void.
            _menu.SetClassFor(player, $"grp{g}", "hidden", options.Count == 0);

            if (options.Count == 0)
                continue;

            _menu.SetVariableFor(player, $"grp{g}_label", Categories[g].Label);

            // Panorama cannot wrap, so the five-per-line split is a real panel per line. Hide the
            // ones this category does not fill or they leave a gap under it.
            for (var line = 0; line * PerRow < Cols; line++)
                _menu.SetClassFor(player, $"grp{g}_r{line}", "hidden", options.Count <= line * PerRow);

            for (var c = 0; c < Cols; c++)
            {
                var tile = $"w{g}_{c}";

                if (c >= options.Count)
                {
                    _menu.SetClassFor(player, tile, "hidden", true);
                    continue;
                }

                var weapon = options[c];

                _menu.SetVariableFor(player, tile, DisplayName(weapon));
                _menu.SetClassFor(player, tile, "hidden", false);
                _menu.SetClassFor(player, tile, "selected",
                    draft.Chosen.TryGetValue(g, out var chosen) && chosen.Equals(weapon));

                ApplyIcon(player, draft, tile, weapon);
            }
        }
    }

    /// <summary>Swaps the tile's weapon picture. The server cannot set an image path, only toggle a
    /// class, so each weapon is a class - and the previous one has to come off first.</summary>
    private void ApplyIcon(CCSPlayerController player, Draft draft, string tile, CsItem weapon)
    {
        if (!IconSlugs.TryGetValue(weapon, out var slug))
            return;   // no artwork mapped - the tile still shows its name

        var wanted = $"icon-{slug}";

        if (draft.Icons.TryGetValue(tile, out var current))
        {
            if (current == wanted)
                return;

            _menu.SetClassFor(player, tile, current, false);
        }

        _menu.SetClassFor(player, tile, wanted, true);
        draft.Icons[tile] = wanted;
    }

    private void OnEvent(PanelEvent e)
    {
        if (e.Action == PanelAction.Close)
        {
            _drafts.Remove(e.Player.SteamID);
            return;
        }

        // A round restart destroys the layout entity, so the library rebuilds it and tells us. Rows
        // and title come back on their own; the grid does not - every tile is a per-viewer write the
        // library never saw the meaning of. The draft survives, so redrawing is enough, and the
        // player keeps their selections across the round.
        if (e.Action == PanelAction.Restored)
        {
            if (_drafts.TryGetValue(e.Player.SteamID, out var restored))
            {
                // Icon classes are tracked per tile to know what to remove; the new entity has none
                // of them, so that record has to be cleared or the first swap removes nothing.
                restored.Icons.Clear();

                Render(e.Player, restored);
                _menu.SetVariableFor(e.Player, "team_t", "Terrorists");
                _menu.SetVariableFor(e.Player, "team_ct", "Counter-Terrorists");
                _menu.SetVariableFor(e.Player, "menu_footer", "Click to choose - Save to keep");
            }

            return;
        }

        if (e.Action != PanelAction.Button)
            return;

        if (!_drafts.TryGetValue(e.Player.SteamID, out var draft))
            return;

        if (e.ElementId == "wsel_save")
        {
            if (!draft.Saving)
                Save(e.Player, draft);

            return;
        }

        if (ParseTile(e.ElementId) is not var (group, column))
            return;

        if (group >= draft.Options.Count || column >= draft.Options[group].Count)
            return;

        var weapon = draft.Options[group][column];

        // Single-select per category, and clicking the current pick clears it - which is how you
        // say "no preference, give me the default" without a separate button for it.
        var wasChosen = draft.Chosen.TryGetValue(group, out var previous) && previous.Equals(weapon);

        if (draft.Chosen.TryGetValue(group, out var old))
        {
            var oldIndex = draft.Options[group].IndexOf(old);

            if (oldIndex >= 0)
                _menu.SetClassFor(e.Player, $"w{group}_{oldIndex}", "selected", false);

            draft.Chosen.Remove(group);
        }

        if (!wasChosen)
        {
            draft.Chosen[group] = weapon;
            _menu.SetClassFor(e.Player, $"w{group}_{column}", "selected", true);
        }
    }

    /// <summary>
    /// Commits the draft.
    ///
    /// <para><b>Off the main thread, deliberately.</b> Each changed category is a SQLite write, and
    /// four of them back to back on the game thread produced a 36ms frame - a visible hitch for
    /// everyone on the server, not just the player who pressed Save. The writes now run on the
    /// thread pool.</para>
    ///
    /// <para><b>And back onto it for the UI.</b> Everything the menu touches goes through native
    /// calls on the layout entity, which are not thread-safe. Continuing on a pool thread after the
    /// awaits and writing the footer from there is the kind of bug that corrupts state under load
    /// rather than failing cleanly, so the result is marshalled back via
    /// <see cref="Server.NextFrame"/>.</para>
    /// </summary>
    private void Save(CCSPlayerController player, Draft draft)
    {
        var steamId = player.SteamID;

        // Snapshot what to write before leaving the game thread - draft is mutated by clicks, and
        // the player may close the menu while the writes are in flight.
        var pending = new List<(int Group, CsItem? Weapon)>();

        for (var g = 0; g < Groups; g++)
        {
            var hasChoice = draft.Chosen.TryGetValue(g, out var chosen);
            var hadChoice = draft.Original.TryGetValue(g, out var original);

            if (hasChoice == hadChoice && (!hasChoice || chosen.Equals(original)))
                continue;

            pending.Add((g, hasChoice ? chosen : null));
        }

        if (pending.Count == 0)
        {
            _menu.Close(player);

            return;
        }

        draft.Saving = true;
        _menu.SetVariableFor(player, "menu_footer", "Saving...");

        Task.Run(async () =>
        {
            foreach (var (group, weapon) in pending)
            {
                var (_, _, team, round) = Categories[group];

                // Route through the same handler the chat command uses, so validation, allocation
                // type resolution and persistence stay in one place.
                var (message, _) = await OnWeaponCommandHelper.HandleAsync(
                    weapon is not null ? [weapon.Value.ToString(), TeamArg(team)] : [],
                    steamId,
                    roundType: round,
                    currentTeam: team,
                    remove: weapon is null);

                if (!string.IsNullOrEmpty(message))
                {
                    _logger.LogDebug(
                        "[WeaponHud] {SteamId} {Category}: {Message}", steamId, Categories[group].Label, message);
                }
            }

            Server.NextFrame(() =>
            {
                if (player is not { IsValid: true })
                    return;

                foreach (var (group, weapon) in pending)
                {
                    if (weapon is null)
                    {
                        draft.Original.Remove(group);
                    }
                    else
                    {
                        draft.Original[group] = weapon.Value;
                    }
                }

                _menu.SetVariableFor(player, "menu_footer", $"Saved {pending.Count} change(s)");
                _menu.Close(player);
            });
        });
    }

    private static (int Group, int Column)? ParseTile(string element)
    {
        if (element.Length < 4 || element[0] != 'w')
            return null;

        var split = element.IndexOf('_');

        if (split < 2
            || !int.TryParse(element.AsSpan(1, split - 1), out var group)
            || !int.TryParse(element.AsSpan(split + 1), out var column))
            return null;

        return (group, column);
    }

    /// <summary>
    /// Weapon to icon-class suffix. Explicit rather than derived from <c>ToString()</c>, because
    /// <c>CsItem</c> has aliased values - <c>CsItem.Bizon</c> prints as "PPBizon", <c>CsItem.R8</c>
    /// as "Revolver" - so the name the enum reports is not the name anything else uses. Keyed on the
    /// member so a typo is a build error instead of a blank tile.
    /// </summary>
    private static readonly Dictionary<CsItem, string> IconSlugs = new()
    {
        [CsItem.AK47] = "ak47",
        [CsItem.Galil] = "galilar",
        [CsItem.Krieg] = "sg556",
        [CsItem.M4A4] = "m4a1",
        [CsItem.M4A1S] = "m4a1_silencer",
        [CsItem.Famas] = "famas",
        [CsItem.AUG] = "aug",
        [CsItem.M249] = "m249",
        [CsItem.Negev] = "negev",
        [CsItem.AWP] = "awp",
        [CsItem.Scout] = "ssg08",
        [CsItem.AutoSniperT] = "g3sg1",
        [CsItem.AutoSniperCT] = "scar20",
        [CsItem.Mac10] = "mac10",
        [CsItem.MP9] = "mp9",
        [CsItem.MP7] = "mp7",
        [CsItem.MP5] = "mp5sd",
        [CsItem.UMP45] = "ump45",
        [CsItem.P90] = "p90",
        [CsItem.Bizon] = "bizon",
        [CsItem.Nova] = "nova",
        [CsItem.XM1014] = "xm1014",
        [CsItem.SawedOff] = "sawedoff",
        [CsItem.MAG7] = "mag7",
        [CsItem.Deagle] = "deagle",
        [CsItem.R8] = "revolver",
        [CsItem.Glock] = "glock",
        [CsItem.USPS] = "usp_silencer",
        [CsItem.P2000] = "hkp2000",
        [CsItem.P250] = "p250",
        [CsItem.FiveSeven] = "fiveseven",
        [CsItem.Tec9] = "tec9",
        [CsItem.CZ] = "cz75a",
        [CsItem.Dualies] = "elite",
    };

    private static string TeamArg(CsTeam team) => team == CsTeam.Terrorist ? "T" : "CT";

    /// <summary>
    /// Tile captions. <c>ToString()</c> is unusable here - it returns enum names like
    /// <c>AutoSniperCT</c> and <c>M4A1S</c>, which do not fit a 78px tile and are not what the weapon
    /// is called anyway. Anything not listed falls through to the enum name uppercased.
    /// </summary>
    private static readonly Dictionary<CsItem, string> DisplayNames = new()
    {
        [CsItem.M4A1S]        = "M4A1-S",
        [CsItem.M4A4]         = "M4A4",
        [CsItem.Krieg]        = "SG 553",
        [CsItem.Galil]        = "GALIL",
        [CsItem.AutoSniperT]  = "G3SG1",
        [CsItem.AutoSniperCT] = "SCAR-20",
        [CsItem.Scout]        = "SSG 08",
        [CsItem.Bizon]        = "PP-BIZON",
        [CsItem.MP5]          = "MP5-SD",
        [CsItem.UMP45]        = "UMP-45",
        [CsItem.SawedOff]     = "SAWED-OFF",
        [CsItem.MAG7]         = "MAG-7",
        [CsItem.USPS]         = "USP-S",
        [CsItem.P2000]        = "P2000",
        [CsItem.FiveSeven]    = "FIVE7",
        [CsItem.CZ]           = "CZ75-A",
        [CsItem.Dualies]      = "DUALIES",
        [CsItem.R8]           = "REVOLVER",
        [CsItem.Deagle]       = "DEAGLE",
        [CsItem.Glock]        = "GLOCK-18",
        [CsItem.Tec9]         = "TEC-9",
        [CsItem.XM1014]       = "XM1014",
        [CsItem.Mac10]        = "MAC-10",
    };

    private static string DisplayName(CsItem weapon)
        => DisplayNames.TryGetValue(weapon, out var name) ? name : weapon.ToString().ToUpperInvariant();
}
