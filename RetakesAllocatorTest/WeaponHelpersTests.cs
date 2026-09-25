using System.Linq;
using CounterStrikeSharp.API.Modules.Utils;
using RetakesAllocatorCore;
using RetakesAllocatorCore.Config;
using RetakesAllocatorCore.Db;

namespace RetakesAllocatorTest;

public class WeaponHelpersTests : BaseTestFixture
{
    [Test]
    [TestCase(true, true, true)]
    [TestCase(true, false, true)]
    [TestCase(false, true, true)]
    [TestCase(false, false, false)]
    public void TestIsWeaponAllocationAllowed(bool allowAfterFreezeTime, bool isFreezeTime, bool expected)
    {
        Configs.OverrideConfigDataForTests(new ConfigData() {AllowAllocationAfterFreezeTime = allowAfterFreezeTime});

        var canAllocate = WeaponHelpers.IsWeaponAllocationAllowed(isFreezeTime);

        Assert.That(canAllocate, Is.EqualTo(expected));
    }

    /// <summary>
    /// CsItem aliases members - 402 is both M4A1 and M4A4, 304 both MP5SD and MP5, 209 both
    /// Revolver and R8 - so ToString() does not name the member you started from, and the name
    /// lookup resolved "M4A1" (which is what CsItem.M4A4.ToString() returns) back to M4A1S. That
    /// wrote M4A1-S every time a player picked M4A4. Nothing may go weapon -> string -> weapon and
    /// come out different.
    /// </summary>
    [Test]
    public void EveryWeaponNameRoundTripsToTheSameItem()
    {
        Assert.Multiple(() =>
        {
            foreach (var weapon in WeaponHelpers.AllWeapons)
            {
                var found = WeaponHelpers.FindValidWeaponsByName(weapon.GetName());

                Assert.That(found, Is.Not.Empty, $"{weapon.GetName()} resolved to nothing");
                Assert.That(found.First(), Is.EqualTo(weapon), $"{weapon.GetName()} resolved to the wrong item");
            }
        });
    }

    /// <summary>
    /// The chat aliases stay put - `!gun m4a1` has always meant the silenced M4, and the exact-name
    /// pass added to the lookup must not out-rank the override table.
    /// </summary>
    [Test]
    [TestCase("m4a1", CsItem.M4A1S)]
    [TestCase("m4a1-s", CsItem.M4A1S)]
    [TestCase("m4a1s", CsItem.M4A1S)]
    [TestCase("m4a4", CsItem.M4A4)]
    [TestCase("mp5", CsItem.MP5SD)]
    [TestCase("usp", CsItem.USPS)]
    [TestCase("p2000", CsItem.HKP2000)]
    [TestCase("cz", CsItem.CZ)]
    [TestCase("r8", CsItem.Revolver)]
    [TestCase("scout", CsItem.SSG08)]
    public void WeaponNameAliasesResolveAsDocumented(string needle, CsItem expected)
    {
        Assert.That(WeaponHelpers.FindValidWeaponsByName(needle).First(), Is.EqualTo(expected));
    }

    [Test]
    public void EnableAllWeaponsConfigAllowsCrossTeamOptions()
    {
        Configs.GetConfigData().EnableAllWeaponsForEveryone = true;

        var team = Utils.ParseTeam("CT");
        var weapons = WeaponHelpers.GetPossibleWeaponsForAllocationType(
            WeaponAllocationType.FullBuyPrimary, team);

        var ak47 = WeaponHelpers.FindValidWeaponsByName("ak47").First();
        var m4a1s = WeaponHelpers.FindValidWeaponsByName("m4a1s").First();

        Assert.That(weapons, Does.Contain(ak47));
        Assert.That(weapons, Does.Contain(m4a1s));
    }

    [Test]
    public void EnemyStuffPreferenceSwapsPrimaryWeapon()
    {
        var config = new ConfigData
        {
            EnableEnemyStuff = 1,
            ChanceForEnemyStuff = 100,
            AllowedWeaponSelectionTypes = new List<WeaponSelectionType>
            {
                WeaponSelectionType.PlayerChoice,
                WeaponSelectionType.Default
            },
        };

        Configs.OverrideConfigDataForTests(config);

        var userSetting = new UserSetting
        {
            EnemyStuffTeamPreference = EnemyStuffTeamPreference.CounterTerrorist,
        };
        userSetting.SetWeaponPreference(
            CsTeam.Terrorist,
            WeaponAllocationType.FullBuyPrimary,
            CsItem.Galil
        );
        userSetting.SetWeaponPreference(
            CsTeam.CounterTerrorist,
            WeaponAllocationType.FullBuyPrimary,
            CsItem.M4A1S
        );

        var selection = WeaponHelpers.GetWeaponsForRoundType(
            RoundType.FullBuy,
            CsTeam.CounterTerrorist,
            userSetting,
            givePreferred: false
        );

        var weapons = selection.Weapons.ToList();

        var primary = weapons.Last();
        var terroristPrimaries =
            WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.FullBuyPrimary, CsTeam.Terrorist);

        Assert.That(primary, Is.EqualTo(CsItem.Galil));
        Assert.That(terroristPrimaries, Does.Contain(primary));
        Assert.That(selection.EnemyStuffGranted, Is.True);
    }

    [Test]
    public void EnemyStuffPreferenceSwapsPistolRoundWeapon()
    {
        var config = new ConfigData
        {
            EnableEnemyStuff = 1,
            ChanceForEnemyStuff = 100,
            AllowedWeaponSelectionTypes = new List<WeaponSelectionType>
            {
                WeaponSelectionType.PlayerChoice,
                WeaponSelectionType.Default
            },
        };

        Configs.OverrideConfigDataForTests(config);

        var userSetting = new UserSetting
        {
            EnemyStuffTeamPreference = EnemyStuffTeamPreference.CounterTerrorist,
        };
        userSetting.SetWeaponPreference(
            CsTeam.Terrorist,
            WeaponAllocationType.PistolRound,
            CsItem.Tec9
        );
        userSetting.SetWeaponPreference(
            CsTeam.CounterTerrorist,
            WeaponAllocationType.PistolRound,
            CsItem.USPS
        );

        var selection = WeaponHelpers.GetWeaponsForRoundType(
            RoundType.Pistol,
            CsTeam.CounterTerrorist,
            userSetting,
            givePreferred: false
        );

        var weapons = selection.Weapons.ToList();

        Assert.That(weapons, Has.Count.EqualTo(1));
        Assert.That(weapons[0], Is.EqualTo(CsItem.Tec9));
        Assert.That(selection.EnemyStuffGranted, Is.True);
    }

    [Test]
    public void EnemyStuffPreferenceHonorsSelectedTeams()
    {
        var config = new ConfigData
        {
            EnableEnemyStuff = 1,
            ChanceForEnemyStuff = 100,
            AllowedWeaponSelectionTypes = new List<WeaponSelectionType>
            {
                WeaponSelectionType.PlayerChoice,
                WeaponSelectionType.Default
            },
        };

        Configs.OverrideConfigDataForTests(config);

        var userSetting = new UserSetting
        {
            EnemyStuffTeamPreference = EnemyStuffTeamPreference.Terrorist,
        };
        userSetting.SetWeaponPreference(
            CsTeam.Terrorist,
            WeaponAllocationType.FullBuyPrimary,
            CsItem.Galil
        );
        userSetting.SetWeaponPreference(
            CsTeam.CounterTerrorist,
            WeaponAllocationType.FullBuyPrimary,
            CsItem.M4A1S
        );

        var ctSelection = WeaponHelpers.GetWeaponsForRoundType(
            RoundType.FullBuy,
            CsTeam.CounterTerrorist,
            userSetting,
            givePreferred: false
        );

        Assert.That(ctSelection.EnemyStuffGranted, Is.False);

        var terroristSelection = WeaponHelpers.GetWeaponsForRoundType(
            RoundType.FullBuy,
            CsTeam.Terrorist,
            userSetting,
            givePreferred: false
        );

        Assert.That(terroristSelection.EnemyStuffGranted, Is.True);
        Assert.That(terroristSelection.Weapons.Last(), Is.EqualTo(CsItem.M4A1S));
    }

    [Test]
    public void EnemyStuffQuotaBlocksSwapWhenUnavailable()
    {
        var config = new ConfigData
        {
            EnableEnemyStuff = 1,
            ChanceForEnemyStuff = 100,
            AllowedWeaponSelectionTypes = new List<WeaponSelectionType>
            {
                WeaponSelectionType.PlayerChoice,
                WeaponSelectionType.Default
            },
        };

        Configs.OverrideConfigDataForTests(config);

        var userSetting = new UserSetting
        {
            EnemyStuffTeamPreference = EnemyStuffTeamPreference.CounterTerrorist,
        };
        userSetting.SetWeaponPreference(
            CsTeam.Terrorist,
            WeaponAllocationType.FullBuyPrimary,
            CsItem.Galil
        );

        var selection = WeaponHelpers.GetWeaponsForRoundType(
            RoundType.FullBuy,
            CsTeam.CounterTerrorist,
            userSetting,
            givePreferred: false,
            enemyStuffQuotaAvailable: false
        );

        var weapons = selection.Weapons.ToList();

        Assert.That(selection.EnemyStuffGranted, Is.False);
        Assert.That(weapons.Last(), Is.EqualTo(CsItem.M4A1S));
    }

    [Test]
    public void EnemyStuffDoesNotSwapSecondaryOnFullBuy()
    {
        var config = new ConfigData
        {
            EnableEnemyStuff = 1,
            ChanceForEnemyStuff = 100,
            AllowedWeaponSelectionTypes = new List<WeaponSelectionType>
            {
                WeaponSelectionType.PlayerChoice
            },
        };

        Configs.OverrideConfigDataForTests(config);

        var userSetting = new UserSetting
        {
            EnemyStuffTeamPreference = EnemyStuffTeamPreference.CounterTerrorist,
        };
        userSetting.SetWeaponPreference(
            CsTeam.CounterTerrorist,
            WeaponAllocationType.FullBuyPrimary,
            CsItem.M249
        );
        userSetting.SetWeaponPreference(
            CsTeam.Terrorist,
            WeaponAllocationType.FullBuyPrimary,
            CsItem.M249
        );
        userSetting.SetWeaponPreference(
            CsTeam.CounterTerrorist,
            WeaponAllocationType.Secondary,
            CsItem.P2000
        );
        userSetting.SetWeaponPreference(
            CsTeam.Terrorist,
            WeaponAllocationType.Secondary,
            CsItem.Glock
        );

        var selection = WeaponHelpers.GetWeaponsForRoundType(
            RoundType.FullBuy,
            CsTeam.CounterTerrorist,
            userSetting,
            givePreferred: false
        );

        var weapons = selection.Weapons.ToList();

        Assert.That(weapons, Has.Count.EqualTo(2));
        Assert.That(weapons[0], Is.EqualTo(CsItem.P2000));
        Assert.That(weapons[1], Is.EqualTo(CsItem.M249));
    }

    [Test]
    public void EnemyStuffHalfBuySwapsSmgOnly()
    {
        var config = new ConfigData
        {
            EnableEnemyStuff = 1,
            ChanceForEnemyStuff = 100,
            AllowedWeaponSelectionTypes = new List<WeaponSelectionType>
            {
                WeaponSelectionType.PlayerChoice
            },
        };

        Configs.OverrideConfigDataForTests(config);

        var userSetting = new UserSetting
        {
            EnemyStuffTeamPreference = EnemyStuffTeamPreference.CounterTerrorist,
        };
        userSetting.SetWeaponPreference(
            CsTeam.CounterTerrorist,
            WeaponAllocationType.HalfBuyPrimary,
            CsItem.MP9
        );
        userSetting.SetWeaponPreference(
            CsTeam.Terrorist,
            WeaponAllocationType.HalfBuyPrimary,
            CsItem.Mac10
        );

        var selection = WeaponHelpers.GetWeaponsForRoundType(
            RoundType.HalfBuy,
            CsTeam.CounterTerrorist,
            userSetting,
            givePreferred: false
        );

        var weapons = selection.Weapons.ToList();

        Assert.That(weapons.Last(), Is.EqualTo(CsItem.Mac10));
        Assert.That(selection.EnemyStuffGranted, Is.True);
    }

    [Test]
    public void EnemyStuffHalfBuyDoesNotSwapShotgun()
    {
        var config = new ConfigData
        {
            EnableEnemyStuff = 1,
            ChanceForEnemyStuff = 100,
            AllowedWeaponSelectionTypes = new List<WeaponSelectionType>
            {
                WeaponSelectionType.PlayerChoice
            },
        };

        Configs.OverrideConfigDataForTests(config);

        var userSetting = new UserSetting
        {
            EnemyStuffTeamPreference = EnemyStuffTeamPreference.CounterTerrorist,
        };
        userSetting.SetWeaponPreference(
            CsTeam.CounterTerrorist,
            WeaponAllocationType.HalfBuyPrimary,
            CsItem.MAG7
        );
        userSetting.SetWeaponPreference(
            CsTeam.Terrorist,
            WeaponAllocationType.HalfBuyPrimary,
            CsItem.SawedOff
        );

        var selection = WeaponHelpers.GetWeaponsForRoundType(
            RoundType.HalfBuy,
            CsTeam.CounterTerrorist,
            userSetting,
            givePreferred: false
        );

        var weapons = selection.Weapons.ToList();

        Assert.That(weapons.Last(), Is.EqualTo(CsItem.MAG7));
        Assert.That(selection.EnemyStuffGranted, Is.False);
    }
}
