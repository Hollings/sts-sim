using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;

namespace StS2Sim;

/// <summary>
/// Manual turn-boundary hook firing for the headless sim. The official path
/// (<c>Hook.AfterTurnEnd</c>) requires <c>LocalContext.NetId</c>; we have no
/// netcode, so we iterate listeners directly. This is the same pattern the
/// real game would do internally — we just skip the netcode wrapper.
/// </summary>
internal static class TurnHooks
{
    public static async Task FireAfterTurnEnd(Harness.CombatHarness h, CombatSide side)
    {
        // Snapshot listeners up front: AfterTurnEnd implementations can
        // mutate the listener list (powers expire, etc.) which would break
        // mid-iteration enumeration.
        foreach (var listener in h.State.IterateHookListeners().ToList())
        {
            await listener.AfterTurnEnd(h.Ctx, side);
        }
    }
}
