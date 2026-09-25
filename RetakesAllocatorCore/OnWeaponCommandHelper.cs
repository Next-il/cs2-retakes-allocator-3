using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesAllocatorCore.Db;
using RetakesAllocatorCore.Config;

namespace RetakesAllocatorCore;

public class OnWeaponCommandHelper
{
    public static string Handle(ICollection<string> args, ulong userId, RoundType? roundType, CsTeam currentTeam,
        bool remove, out CsItem? outWeapon)
    {
        var result = HandleAsync(args, userId, roundType, currentTeam, remove).GetAwaiter().GetResult();
        outWeapon = result.Item2;
        return result.Item1;
    }

    /// <summary>
    /// Sync wrapper over the <see cref="CsItem"/> overload - use this rather than stringifying the
    /// enum, see that overload for why.
    /// </summary>
    public static string Handle(CsItem weapon, ulong userId, RoundType? roundType, CsTeam currentTeam,
        bool remove, out CsItem? outWeapon, CsTeam? team = null)
    {
        var result = HandleAsync(weapon, userId, roundType, currentTeam, remove, team).GetAwaiter().GetResult();
        outWeapon = result.Item2;
        return result.Item1;
    }

    public static async Task<Tuple<string, CsItem?>> HandleAsync(ICollection<string> args, ulong userId,
        RoundType? roundType, CsTeam currentTeam,
        bool remove)
    {
        CsItem? outWeapon = null;

        Tuple<string, CsItem?> Ret(string str) => new(str, outWeapon);

        if (!Configs.GetConfigData().CanPlayersSelectWeapons())
        {
            return Ret(Translator.Instance["weapon_preference.cannot_choose"]);
        }

        if (args.Count == 0)
        {
            var gunsMessage = Translator.Instance[
                "weapon_preference.gun_usage",
                currentTeam,
                string.Join(", ",
                    WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.PistolRound, currentTeam)),
                string.Join(", ",
                    WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.HalfBuyPrimary,
                        currentTeam)),
                string.Join(", ",
                    WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.FullBuyPrimary, currentTeam))
            ];
            return Ret(gunsMessage);
        }

        var weaponInput = args.ElementAt(0).Trim();

        CsTeam team;
        var teamInput = args.ElementAtOrDefault(1)?.Trim().ToLower();
        if (teamInput is not null)
        {
            var parsedTeamInput = Utils.ParseTeam(teamInput);
            if (parsedTeamInput == CsTeam.None)
            {
                return Ret(Translator.Instance["weapon_preference.invalid_team", teamInput]);
            }

            team = parsedTeamInput;
        }
        else if (currentTeam is CsTeam.None or CsTeam.Spectator)
        {
            return Ret(Translator.Instance["weapon_preference.join_team"]);
        }
        else
        {
            team = currentTeam;
        }

        var foundWeapons = WeaponHelpers.FindValidWeaponsByName(weaponInput);
        if (foundWeapons.Count == 0)
        {
            return Ret(Translator.Instance["weapon_preference.not_found", weaponInput]);
        }

        return await HandleResolvedAsync(foundWeapons.First(), userId, roundType, currentTeam, team, remove);
    }

    /// <summary>
    /// The overload anything holding a real <see cref="CsItem"/> must use - menus, the AWP/scout
    /// commands, the pickup handler.
    ///
    /// <para>Going through the string overload instead was a data-loss bug: CsItem has aliased
    /// members (402 is both <c>M4A1</c> and <c>M4A4</c>, 304 both <c>MP5SD</c> and <c>MP5</c>, ...),
    /// so <c>ToString()</c> does not round trip - <c>CsItem.M4A4.ToString()</c> yields "M4A1", which
    /// the name lookup resolves back to <c>M4A1S</c>. Players picked M4A4 in the HUD menu and had
    /// M4A1-S written to the database. The enum value cannot collide, so it is what we pass.</para>
    ///
    /// <para><paramref name="team"/> is the team whose loadout is being edited, which is not always
    /// the team the player is on - the HUD menu edits both.</para>
    /// </summary>
    public static async Task<Tuple<string, CsItem?>> HandleAsync(CsItem weapon, ulong userId,
        RoundType? roundType, CsTeam currentTeam, bool remove, CsTeam? team = null)
    {
        if (!Configs.GetConfigData().CanPlayersSelectWeapons())
        {
            return new Tuple<string, CsItem?>(Translator.Instance["weapon_preference.cannot_choose"], null);
        }

        var targetTeam = team ?? currentTeam;
        if (targetTeam is CsTeam.None or CsTeam.Spectator)
        {
            return new Tuple<string, CsItem?>(Translator.Instance["weapon_preference.join_team"], null);
        }

        return await HandleResolvedAsync(weapon, userId, roundType, currentTeam, targetTeam, remove);
    }

    /// <summary>
    /// Everything past weapon resolution - validation, allocation type, persistence. Shared by both
    /// overloads so the name-lookup path and the enum path cannot drift apart.
    /// </summary>
    private static async Task<Tuple<string, CsItem?>> HandleResolvedAsync(CsItem weapon, ulong userId,
        RoundType? roundType, CsTeam currentTeam, CsTeam team, bool remove)
    {
        CsItem? outWeapon = null;

        Tuple<string, CsItem?> Ret(string str) => new(str, outWeapon);

        if (!WeaponHelpers.IsUsableWeapon(weapon))
        {
            return Ret(Translator.Instance["weapon_preference.not_allowed", weapon.GetName()]);
        }

        var weaponRoundTypes = WeaponHelpers.GetRoundTypesForWeapon(weapon);
        if (weaponRoundTypes.Count == 0)
        {
            return Ret(Translator.Instance["weapon_preference.invalid_weapon", weapon.GetName()]);
        }

        var allocationType = WeaponHelpers.GetWeaponAllocationTypeForWeaponAndRound(
            roundType, team, weapon
        );
        var isPreferred = allocationType == WeaponAllocationType.Preferred;

        var allocateImmediately = (
            // Always true for pistols
            allocationType is not null &&
            roundType is not null &&
            weaponRoundTypes.Contains(roundType.Value) &&
            // Only set the outWeapon if the user is setting the preference for their current team
            currentTeam == team &&
            // TODO Allow immediate allocation of preferred if the config permits it (eg. unlimited preferred)
            // Could be tricky for max # per team config, since this function doesnt know # of players on the team
            !isPreferred
        );

        if (allocationType is null)
        {
            return Ret(Translator.Instance["weapon_preference.not_valid_for_team", weapon.GetName(), team]);
        }


        if (remove)
        {
            if (isPreferred)
            {
                await Queries.SetAwpWeaponPreferenceAsync(userId, null);
                return Ret(Translator.Instance["weapon_preference.unset_preference_preferred", weapon.GetName()]);
            }
            else
            {
                await Queries.SetWeaponPreferenceForUserAsync(userId, team, allocationType.Value, null);
                return Ret(Translator.Instance[
                    "weapon_preference.unset_preference",
                    weapon.GetName(),
                    GetRoundDisplayName(allocationType.Value, roundType),
                    Utils.TeamString(team, true)
                ]);
            }
        }

        string message;
        if (isPreferred)
        {
            await Queries.SetAwpWeaponPreferenceAsync(userId, weapon);
            // If we ever add more preferred weapons, we need to change the wording of "sniper" here
            message = Translator.Instance["weapon_preference.set_preference_preferred", weapon.GetName()];
        }
        else
        {
            await Queries.SetWeaponPreferenceForUserAsync(userId, team, allocationType.Value, weapon);
            message = Translator.Instance[
                "weapon_preference.set_preference",
                weapon.GetName(),
                GetRoundDisplayName(allocationType.Value, roundType),
                Utils.TeamString(team, true)
            ];
        }

        if (allocateImmediately)
        {
            outWeapon = weapon;
        }

        if (userId == 0)
        {
            message = Translator.Instance["weapon_preference.not_saved"];
        }

        return Ret(message);
    }

    private static string GetRoundDisplayName(WeaponAllocationType allocationType, RoundType? roundType)
    {
        return allocationType switch
        {
            WeaponAllocationType.PistolRound => Translator.Instance["weapon_preference.round.pistol"],
            WeaponAllocationType.HalfBuyPrimary => Translator.Instance["weapon_preference.round.half_buy"],
            WeaponAllocationType.FullBuyPrimary => Translator.Instance["weapon_preference.round.full_buy"],
            WeaponAllocationType.Secondary when roundType == RoundType.HalfBuy => Translator.Instance["weapon_preference.round.half_buy"],
            WeaponAllocationType.Secondary when roundType == RoundType.FullBuy => Translator.Instance["weapon_preference.round.full_buy"],
            WeaponAllocationType.Secondary => Translator.Instance["weapon_preference.round.buy"],
            _ => allocationType.ToString(),
        };
    }

}


