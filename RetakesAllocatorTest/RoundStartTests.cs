using CounterStrikeSharp.API.Modules.Utils;
using RetakesAllocatorCore;
using RetakesAllocatorCore.Managers;

namespace RetakesAllocatorTest;

public class RoundStartTests : BaseTestFixture
{
    [Test]
    public void TestRoundStartCanRunInCore()
    {
        OnRoundPostStartHelper.Handle(
            new List<int>(),
            i => 1,
            x => CsTeam.None,
            x => {},
            (x, y, z) => {},
            x => true,
            x => true,
            x => true,
            out _
        );
    }

    [TestCase(RoundType.Pistol, "slot2")]
    [TestCase(RoundType.HalfBuy, "slot1")]
    [TestCase(RoundType.FullBuy, "slot1")]
    public void BothTeamsSelectTheGunSlot(RoundType roundType, string expectedSlot)
    {
        RoundTypeManager.Instance.SetNextRoundTypeOverride(roundType);
        try
        {
            var players = new List<int> {1, 2, 3, 4};
            var slots = new Dictionary<int, string?>();
            OnRoundPostStartHelper.Handle(
                players,
                p => (ulong) p,
                p => p <= 2 ? CsTeam.Terrorist : CsTeam.CounterTerrorist,
                _ => { },
                (player, _, slot) => { slots[player] = slot; },
                _ => true,
                _ => true,
                _ => true,
                out _
            );

            Assert.That(slots, Has.Count.EqualTo(players.Count));
            // Terrorists used to get "slot5" (the C4 slot), which is never occupied on a retakes
            // server, so the switch never happened for them.
            Assert.That(slots.Values, Is.All.EqualTo(expectedSlot));
        }
        finally
        {
            RoundTypeManager.Instance.SetNextRoundTypeOverride(null);
        }
    }

    [TestCase(RoundType.HalfBuy)]
    [TestCase(RoundType.FullBuy)]
    public void PrimaryIsGivenBeforeThePistol(RoundType roundType)
    {
        RoundTypeManager.Instance.SetNextRoundTypeOverride(roundType);
        try
        {
            var players = new List<int> {1, 2, 3, 4};
            var itemsByPlayer = new Dictionary<int, List<CsItem>>();
            OnRoundPostStartHelper.Handle(
                players,
                p => (ulong) p,
                p => p <= 2 ? CsTeam.Terrorist : CsTeam.CounterTerrorist,
                _ => { },
                (player, items, _) => { itemsByPlayer[player] = items.ToList(); },
                _ => true,
                _ => true,
                _ => true,
                out _
            );

            Assert.That(itemsByPlayer, Has.Count.EqualTo(players.Count));
            foreach (var items in itemsByPlayer.Values)
            {
                var slotTypes = items.Select(item => WeaponHelpers.GetSlotTypeForItem(item)).ToList();
                var primaryIndex = slotTypes.IndexOf(ItemSlotType.Primary);
                var secondaryIndex = slotTypes.IndexOf(ItemSlotType.Secondary);

                Assert.That(primaryIndex, Is.Not.EqualTo(-1));
                Assert.That(secondaryIndex, Is.Not.EqualTo(-1));
                // The first gun handed out is the one the player ends up holding.
                Assert.That(primaryIndex, Is.LessThan(secondaryIndex));
            }
        }
        finally
        {
            RoundTypeManager.Instance.SetNextRoundTypeOverride(null);
        }
    }
}
