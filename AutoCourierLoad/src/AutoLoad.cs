using System;
using System.Collections.Generic;
using HarmonyLib;
using Photon.Pun;
using SupermarketSimulator.Courier;
using UnityEngine;
using CourierNpc = SupermarketSimulator.Courier.Courier;

namespace AutoCourierLoad;

/// <summary>
/// Improvement #12: when the courier comes back from a delivery he takes the closed, name-tagged paper bags
/// lying around his parking space (as if a player had handed them to him) and, once his trunk covers every
/// active online order, is full, or an order is nearly out of time, leaves as if a player had pressed E.
///
/// What the game does (native analysis of GameAssembly.dll):
///  - The courier is a state machine (Courier.m_StateMachine; states Idle, Delivery, ReturnBag), run on the
///    master client only. A delivery is a sequence inside CourierDeliveryState whose last task,
///    CourierNotifyReturn, calls Courier.NotifyReturned (master: SetDispatched(false), a "courier is back"
///    toast, NetworkCourier.Returned_Broadcast). When the sequence is done the state machine goes back to
///    CourierIdleState, whose Enter dismounts the scooter, recalls it to the park spot (EmployeeManager.
///    GetCourierParkPosition = Courier.ParkPosition), drops invalid parcels and opens the trunk lid.
///    "Back and waiting at the park" is Courier.IsAvailableForHandOver (setup present, not riding, not
///    dispatched, scooter at the park or the park blocked) together with the active state being Idle.
///  - Handing a bag over by hand is OnlineOrderInteraction.GiveBagToCourier: the player drops the bag from his
///    hands, clears his claim on the order (CourierManager.SetPlayerClaim(id, false); ReceiveBag does this itself
///    too), and the master runs
///    Courier.ReceiveBag(bag) (a guest instead transfers the bag's PhotonView to the master and sends
///    NetworkCourier.GiveBag_Master -> CourierGiveBag_RPC -> ReceiveBag). ReceiveBag, on the master, requires
///    state == Idle, IsAvailableForHandOver, ParcelCount < MaxOrderCount (the trunk limit, 3 for a new courier,
///    from the CourierSO level) and Courier.TryResolveHandOverOrder(bag): the bag is not open (m_IsOpen
///    false), has its tag attached (IsTagAttached), has an order (m_OrderData), OrderManager.GetOrder(id)
///    still returns it with IsExpired false, and the order is not already claimed by a courier. Then it
///    claims the order (CourierManager.m_ClaimedOrders + Claim_Broadcast) and StowParcel: OnHeld (physics
///    off, collider off), StowBag (attach to the scooter's bag mount), the trunk visuals, SetHolder_Broadcast
///    ("courier_<id>" as the bag's holder, so every client sees it as carried) and ParcelAdded_Broadcast.
///    If any check fails ReceiveBag instead throws the bag into the CourierBagDropArea (MoveBagToDropArea).
///  - The E press on the courier is Courier.CustomInteract: when CanDispatch (not dispatched, not riding,
///    at least one parcel) the master calls RequestDispatch() (a guest sends NetworkCourier.Dispatch_Master ->
///    CourierDispatch_RPC, which ends in the same call). RequestDispatch needs the Idle state, CanDispatch and
///    HasDeliverableParcel and then SetDispatched(true), which the Idle -> Delivery transition reacts to.
///  - Bags are networked objects (NetworkOrderPaperBag, Photon-instantiated). OnlineOrderPaperBagContainer.
///    m_PaperBags lists them all; IsCarried(bag) is true when the bag has a parent transform or (online) its
///    NetworkOrderPaperBag.m_Holder is non-empty, i.e. a player holds it or it sits in a courier's hands or
///    trunk. A player picking a bag up (TakePaperBag) takes over its PhotonView; placing one on a placement
///    area hands it back to the master.
///  - An order's deadline: OrderListData.HasExpiration, ExpirationDay, ExpirationTime (game seconds of the
///    day, same axis as DayCycleManager.CurrentTime). The countdown on the note (OrderTimeDisplay.Update) is
///    (ExpirationTime - CurrentTime) in game seconds, plus OrderManager.m_DayEndTime (46800) when
///    ExpirationDay is after the current day, divided by DayCycleManager.GameTimeScale = real seconds, shown
///    as MM:SS (yellow under 240 s, red under 120 s). OnlineOrderCustomer.CheckExpiration expires the order
///    when that reaches zero. "Less than a minute left" in this mod is that same number.
///
/// What this mod does (host / solo only; a guest does nothing here):
///  - A postfix on Courier.NotifyReturned starts a "visit" for that courier. A per-frame behaviour (every
///    0.4 s) looks at each visit: while the courier is still returning it only watches the bags near his
///    park spot (so a bag that was already lying there counts as settled when he arrives); once he is Idle,
///    IsAvailableForHandOver and not dispatched, it scans every bag in OnlineOrderPaperBagContainer.m_PaperBags
///    for the first time and then again for KeepScanningSeconds (or for as long as he waits with something
///    loaded and not yet leaving). A bag qualifies when it is active, not
///    IsCarried (nobody holds it, it is not in a trunk), within ScanRadius metres of Courier.ParkPosition,
///    passes the game's own TryResolveHandOverOrder, has been lying free for at least 0.75 s, and was not
///    thrown into the drop area by the courier himself (see below). Bags with the least time left go first.
///  - Each one goes through the game's own Courier.ReceiveBag, after the same preparation as the host's
///    hand-over: the bag's PhotonView is taken over if another client owns it and the player's "working on
///    this order" claim is cleared. Everything after that is vanilla (claim broadcast, holder broadcast,
///    ParcelAdded broadcast, trunk visuals), so guests, modded or not, see exactly what they see when the
///    host hands a bag over. Loading stops at min(MaxBags, MaxOrderCount) or when the courier stops being
///    ready; the mod checks the conditions ReceiveBag checks first, because a failed ReceiveBag would throw
///    the bag into the drop area.
///  - After the last load (1.2 s later, a short pause so the trunk state has reached everybody) the mod decides whether he leaves:
///    one of the orders in his trunk has under LeaveUnderSeconds left (per the countdown above), or no
///    active order is left unclaimed by a courier (OrderManager.GeneratedOrderLists, not expired, all in
///    CourierManager.m_ClaimedOrders), or the trunk is full (LeaveWhenFull). Then it calls
///    Courier.RequestDispatch(), the exact master-side effect of pressing E.
///  - A postfix on Courier.MoveBagToDropArea remembers the bags the courier himself put in the drop area (a
///    rejected hand-over, or a released parcel); they are not loaded again until someone moves them, so the
///    courier can't loop on a bag he just gave back.
///
/// Multiplayer: the courier AI, the claim table and RequestDispatch are master-side, so this runs only on the
/// host (PhotonNetwork.IsConnectedAndSlave -> nothing). It makes no network calls of its own: it invokes the
/// game's methods, which broadcast. An unmodded guest therefore stays consistent. Bags a player holds (host
/// or guest) are IsCarried and never touched; a guest's bag counts as held from the moment the host handled
/// the guest's take request (Take_RPC sets the holder / parent).
///
/// Not verified in-game (no way to run the game here); see CLAUDE.md for the test steps.
/// </summary>
internal static class AutoLoad
{
    private const float TickInterval = 0.4f;
    private const float SettleSeconds = 0.75f;
    private const float DepartDelay = 1.2f;          // stowing is instant (no tween); this is just a short pause
    private const float MinScanWindow = 1.5f;
    private const float GiveUpSeconds = 180f;       // a visit that never becomes ready (courier dismissed, ...)
    private const float DefaultDayEnd = 46800f;
    private const int FallbackTrunk = 3;

    private sealed class Visit
    {
        public CourierNpc Courier;
        public IntPtr Ptr;
        public float ArmedAt;
        public float ReadyAt = -1f;
        public int Scans;
        public int Loaded;
        public float LastLoadAt;
        public float NextDispatchTry;
        public readonly Dictionary<int, float> FirstSeen = new();
    }

    private static readonly List<Visit> s_visits = new();
    /// <summary>Bags the courier threw into the drop area: instance id -> where. Ignored until moved.</summary>
    private static readonly Dictionary<int, Vector3> s_ignored = new();
    private static float s_next;
    private static bool s_loggedError;

    internal static void Reset()
    {
        s_visits.Clear();
        s_ignored.Clear();
        s_loggedError = false;
    }

    internal static void LogError(string where, Exception e)
    {
        if (s_loggedError) return;
        s_loggedError = true;
        Plugin.Logger.LogError($"[AutoCourier] {where} failed (further errors suppressed): {e}");
    }

    private static void Log(string msg)
    {
        if (Plugin.Diagnostics != null && Plugin.Diagnostics.Value) Plugin.Logger.LogInfo($"[AutoCourier] {msg}");
    }

    private static bool IsGuest => PhotonNetwork.IsConnectedAndSlave;

    // ---- events from the game ---------------------------------------------------------------------

    /// <summary>Courier.NotifyReturned postfix (master only): the courier is back from a delivery.</summary>
    internal static void OnReturned(CourierNpc courier)
    {
        if (!Plugin.Active || courier == null || IsGuest) return;
        foreach (var v in s_visits)
        {
            if (v.Ptr != courier.Pointer) continue;
            // Back again before the old visit ended: start it afresh.
            v.ArmedAt = Time.time;
            v.ReadyAt = -1f;
            v.Scans = 0;
            v.Loaded = 0;
            v.FirstSeen.Clear();
            return;
        }
        s_visits.Add(new Visit { Courier = courier, Ptr = courier.Pointer, ArmedAt = Time.time });
        Log($"courier #{courier.EmployeeId} is back from a delivery; watching {Plugin.ScanRadius.Value:0.#} m around his parking space");
    }

    /// <summary>Courier.MoveBagToDropArea postfix: the courier gave this bag back; don't load it again until it is moved.</summary>
    internal static void OnBagReturnedToArea(OrderPaperBag bag)
    {
        if (!Plugin.Active || bag == null) return;
        if (s_ignored.Count > 200) s_ignored.Clear();
        s_ignored[bag.GetInstanceID()] = bag.transform.position;
    }

    // ---- per-frame work ---------------------------------------------------------------------------

    internal static void Tick()
    {
        if (!Plugin.Active || s_visits.Count == 0) return;
        float now = Time.time;
        if (now < s_next) return;
        s_next = now + TickInterval;
        if (IsGuest) { s_visits.Clear(); return; }

        for (int i = s_visits.Count - 1; i >= 0; i--)
        {
            bool keep;
            try { keep = Step(s_visits[i], now); }
            catch (Exception e) { LogError("visit", e); keep = false; }
            if (!keep) s_visits.RemoveAt(i);
        }
    }

    /// <summary>One look at a visit. Returns false when it is over.</summary>
    private static bool Step(Visit v, float now)
    {
        var c = v.Courier;
        if (c == null) return false; // destroyed (dismissed, scene change)

        bool idle = IsIdle(c);
        if (v.ReadyAt < 0f)
        {
            if (now - v.ArmedAt > GiveUpSeconds) { Log($"courier #{c.EmployeeId}: never became ready, giving up"); return false; }
            Collect(v, c, now, null);                // only starts the settle timers
            if (!idle || c.IsDispatched || !c.IsAvailableForHandOver) return true;
            v.ReadyAt = now;
            Log($"courier #{c.EmployeeId} is waiting at his parking space (trunk {c.ParcelCount}/{Capacity(c)})");
        }
        else
        {
            if (c.IsDispatched || !idle)
            {
                Log($"courier #{c.EmployeeId} left or got busy; visit over ({v.Loaded} bag(s) loaded)");
                return false;
            }
            if (!c.IsAvailableForHandOver) return true;
        }

        // Even "0 = only when he arrives" lasts a moment, so that bags that were lying there before count as settled.
        // While he waits with something loaded, keep looking: the bag he is waiting for may arrive late.
        bool scanning = v.Scans == 0 || v.Loaded > 0 || now - v.ReadyAt <= Math.Max(Plugin.KeepScanningSeconds.Value, MinScanWindow);
        if (scanning)
        {
            v.Scans++;
            LoadCandidates(v, c, now);
        }

        if (v.Loaded > 0 && now - v.LastLoadAt >= DepartDelay && now >= v.NextDispatchTry && TryLeave(v, c, now))
            return false;

        if (!scanning && v.Loaded == 0)
        {
            Log($"courier #{c.EmployeeId}: nothing to load; visit over");
            return false;
        }
        return true;
    }

    private static bool IsIdle(CourierNpc c)
    {
        var sm = c.m_StateMachine;
        var idle = c.m_IdleState;
        if (sm == null || idle == null) return false;
        var active = sm.ActiveState;
        return active != null && active.Pointer == idle.Pointer;
    }

    /// <summary>How many bags this courier may hold: his trunk limit, optionally lowered by MaxBags.</summary>
    private static int Capacity(CourierNpc c)
    {
        int cap;
        try { cap = c.MaxOrderCount; }
        catch (Exception) { cap = FallbackTrunk; }
        if (cap < 1) cap = FallbackTrunk;
        int user = Plugin.MaxBags.Value;
        return user > 0 ? Math.Min(cap, user) : cap;
    }

    // ---- finding bags -----------------------------------------------------------------------------

    private struct Candidate
    {
        public OrderPaperBag Bag;
        public OrderListData Order;
        public float Distance;
        public float TimeLeft;
    }

    private static Vector3 ParkPoint(CourierNpc c)
    {
        var t = c.ParkPosition;
        if (t == null) t = c.WaitingPosition;
        if (t == null) t = c.transform;
        return t.position;
    }

    /// <summary>
    /// Updates the settle timers and, when <paramref name="into"/> is given, lists the bags that have been
    /// lying free in the area for at least <see cref="SettleSeconds"/>.
    /// </summary>
    private static void Collect(Visit v, CourierNpc c, float now, List<Candidate> into)
    {
        var bags = OnlineOrderPaperBagContainer.m_PaperBags;
        if (bags == null) return;
        var park = ParkPoint(c);
        float radius = Plugin.ScanRadius.Value;
        var present = new HashSet<int>();

        for (int i = 0; i < bags.Count; i++)
        {
            var bag = bags[i];
            if (bag == null) continue;
            int id = bag.GetInstanceID();
            var go = bag.gameObject;
            if (go == null || !go.activeInHierarchy) continue;

            if (OnlineOrderPaperBagContainer.IsCarried(bag)) { s_ignored.Remove(id); continue; }
            if (s_ignored.TryGetValue(id, out var where))
            {
                if ((bag.transform.position - where).sqrMagnitude < 0.25f) continue; // still where the courier left it
                s_ignored.Remove(id);
            }

            float dist = Vector3.Distance(bag.transform.position, park);
            if (dist > radius) continue;
            OrderListData order;
            if (!CourierNpc.TryResolveHandOverOrder(bag, out order) || order == null) continue;

            present.Add(id);
            if (!v.FirstSeen.TryGetValue(id, out var first)) { first = now; v.FirstSeen[id] = now; }
            if (into != null && now - first >= SettleSeconds)
                into.Add(new Candidate { Bag = bag, Order = order, Distance = dist, TimeLeft = TimeLeft(order) });
        }

        if (v.FirstSeen.Count > present.Count)
        {
            var gone = new List<int>();
            foreach (var k in v.FirstSeen.Keys)
                if (!present.Contains(k)) gone.Add(k);
            foreach (var k in gone) v.FirstSeen.Remove(k);
        }
    }

    private static void LoadCandidates(Visit v, CourierNpc c, float now)
    {
        int cap = Capacity(c);
        if (c.ParcelCount >= cap || c.IsFull) { Collect(v, c, now, null); return; }

        var list = new List<Candidate>();
        Collect(v, c, now, list);
        if (list.Count == 0) return;
        list.Sort((a, b) =>
        {
            int t = a.TimeLeft.CompareTo(b.TimeLeft);
            return t != 0 ? t : a.Distance.CompareTo(b.Distance);
        });

        foreach (var cand in list)
        {
            if (c.ParcelCount >= cap || c.IsFull) break;
            // ReceiveBag throws the bag into the drop area when any of these fail: check them first.
            if (!IsIdle(c) || c.IsDispatched || !c.IsAvailableForHandOver) break;
            if (cand.Bag == null) continue;
            if (OnlineOrderPaperBagContainer.IsCarried(cand.Bag)) continue; // picked up since the scan
            if (Load(c, cand, cap))
            {
                v.Loaded++;
                v.LastLoadAt = now;
                v.FirstSeen.Remove(cand.Bag.GetInstanceID());
            }
        }
    }

    private static bool Load(CourierNpc c, Candidate cand, int cap)
    {
        var bag = cand.Bag;
        var order = cand.Order;
        try
        {
            // Like a player's hand-over: take the bag over first. (ReceiveBag itself drops the player claim.)
            if (PhotonNetwork.IsConnected)
            {
                var nb = bag.NetworkOrderPaperBag;
                var pv = nb != null ? nb.photonView : null;
                if (pv != null && !pv.IsMine && PhotonNetwork.LocalPlayer != null)
                    pv.TransferOwnership(PhotonNetwork.LocalPlayer);
            }

            bool ok = c.ReceiveBag(bag);
            if (ok)
            {
                Log($"loaded the bag for order #{order.ID} ({order.CustomerName}) into courier #{c.EmployeeId}'s trunk " +
                    $"({c.ParcelCount}/{cap}); {Describe(cand.TimeLeft)}, {cand.Distance:0.0} m from his parking space");
                return true;
            }
            // ReceiveBag moved the bag into the drop area (MoveBagToDropArea patch has noted it).
            Plugin.Logger.LogWarning($"[AutoCourier] the courier refused the bag for order #{order.ID}; it was put back in his drop area");
            return false;
        }
        catch (Exception e)
        {
            LogError("loading a bag", e);
            return false;
        }
    }

    // ---- leaving ----------------------------------------------------------------------------------

    private static bool TryLeave(Visit v, CourierNpc c, float now)
    {
        if (!IsIdle(c) || !c.CanDispatch || !c.HasDeliverableParcel) return false;

        string reason = null;
        float threshold = Plugin.LeaveUnderSeconds.Value;
        if (threshold > 0f)
        {
            var parcels = c.m_Parcels;
            for (int i = 0; parcels != null && i < parcels.Count; i++)
            {
                var order = parcels[i]?.Order;
                if (order == null) continue;
                float left = TimeLeft(order);
                if (left < threshold) { reason = $"order #{order.ID} has only {Math.Max(left, 0f):0} s left"; break; }
            }
        }
        if (reason == null && NoOrderWaiting())
            reason = "his trunk covers every active order";
        if (reason == null && Plugin.LeaveWhenFull.Value && (c.IsFull || c.ParcelCount >= Capacity(c)))
            reason = "his trunk is full";
        if (reason == null) return false;

        bool ok = c.RequestDispatch(); // what E does on the master (Courier.CustomInteract)
        if (ok)
        {
            Log($"courier #{c.EmployeeId} leaves with {c.ParcelCount} bag(s): {reason}");
            return true;
        }
        v.NextDispatchTry = now + 3f;
        Log($"courier #{c.EmployeeId}: wanted to leave ({reason}) but RequestDispatch refused; will retry");
        return false;
    }

    /// <summary>True when every active (not expired) order is already claimed by a courier.</summary>
    private static bool NoOrderWaiting()
    {
        if (!NoktaSingleton<OrderManager>.HasInstance || !NoktaSingleton<CourierManager>.HasInstance) return false;
        var list = NoktaSingleton<OrderManager>.Instance.GeneratedOrderLists;
        var cm = NoktaSingleton<CourierManager>.Instance;
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
        {
            var o = list[i];
            if (o == null || o.IsExpired) continue;
            if (!cm.IsClaimedByCourier(o.ID)) return false;
        }
        return true;
    }

    // ---- time left --------------------------------------------------------------------------------

    /// <summary>
    /// Real seconds until the order expires, as the countdown on its note shows it
    /// (OrderTimeDisplay.Update). +infinity for an order without a deadline.
    /// </summary>
    internal static float TimeLeft(OrderListData order)
    {
        if (order == null || !order.HasExpiration || !NoktaSingleton<DayCycleManager>.HasInstance)
            return float.PositiveInfinity;
        var dm = NoktaSingleton<DayCycleManager>.Instance;
        float dayEnd = DefaultDayEnd;
        if (NoktaSingleton<OrderManager>.HasInstance)
        {
            float d = NoktaSingleton<OrderManager>.Instance.m_DayEndTime;
            if (d > 0f) dayEnd = d;
        }
        float left = order.ExpirationDay > dm.CurrentDay
            ? order.ExpirationTime + dayEnd - dm.CurrentTime
            : order.ExpirationTime - dm.CurrentTime;
        float scale = dm.GameTimeScale;
        if (scale < 0.0001f) scale = 1f;
        return left / scale;
    }

    private static string Describe(float seconds) =>
        float.IsInfinity(seconds) ? "no deadline" : $"{Math.Max(seconds, 0f):0} s left";
}

[HarmonyPatch(typeof(CourierNpc), nameof(CourierNpc.NotifyReturned))]
internal static class AutoCourier_NotifyReturned_Patch
{
    private static void Postfix(CourierNpc __instance)
    {
        try { AutoLoad.OnReturned(__instance); }
        catch (Exception e) { AutoLoad.LogError("NotifyReturned postfix", e); }
    }
}

[HarmonyPatch(typeof(CourierNpc), nameof(CourierNpc.MoveBagToDropArea))]
internal static class AutoCourier_MoveBagToDropArea_Patch
{
    private static void Postfix(OrderPaperBag bag)
    {
        try { AutoLoad.OnBagReturnedToArea(bag); }
        catch (Exception e) { AutoLoad.LogError("MoveBagToDropArea postfix", e); }
    }
}
