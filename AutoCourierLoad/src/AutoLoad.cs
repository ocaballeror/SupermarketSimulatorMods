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
///    trunk, or (offline) PlayerObjectHolder.m_CurrentObject is the bag, a field OnlineOrderInteraction never
///    clears on drop, so the offline branch stays true for the last bag dropped (this mod uses its own IsHeld). A player picking a bag up (TakePaperBag) takes over its PhotonView; placing one on a placement
///    area hands it back to the master.
///  - An order's deadline: OrderListData.HasExpiration, ExpirationDay, ExpirationTime (game seconds of the
///    day, same axis as DayCycleManager.CurrentTime). The countdown on the note (OrderTimeDisplay.Update) is
///    (ExpirationTime - CurrentTime) in game seconds, plus OrderTimeDisplay.m_DayEndTime (46800, same value as OrderManager's) when
///    ExpirationDay is after the current day, divided by DayCycleManager.GameTimeScale = real seconds, shown
///    as MM:SS (yellow under 240 s, red under 120 s). OnlineOrderCustomer.CheckExpiration expires the order
///    when that reaches zero. "Less than a minute left" in this mod is that same number.
///
/// What this mod does (host / solo only; a guest does nothing here):
///  - A "visit" for a courier starts in two ways: a postfix on Courier.NotifyReturned (he just came back), and an
///    idle watch (every 1 s, EmployeeManager.ActiveCouriers) for a courier who has no visit but is already
///    waiting: state Idle, not dispatched, IsAvailableForHandOver, trunk not full, and at least one bag that
///    could be loaded lies within ScanRadius of his park spot (a bag dropped next to him while he waits, a
///    courier already waiting after a save load, or one whose last visit ran out). Everything after the start
///    is the same for both.
///  - A per-frame behaviour (every
///    0.4 s) looks at each visit: while the courier is still returning it only watches the bags near his
///    park spot (so a bag that was already lying there counts as settled when he arrives); once he is Idle,
///    IsAvailableForHandOver and not dispatched, it scans every bag in OnlineOrderPaperBagContainer.m_PaperBags
///    for the first time and then again for ScanWindow (1.5 s; or for as long as he waits with something
///    loaded and not yet leaving). A bag qualifies when it is active, not
///    IsHeld (nobody holds it, it is not in a trunk; see IsHeld for why not the game's IsCarried), within ScanRadius
///    metres of Courier.ParkPosition,
///    passes the game's own TryResolveHandOverOrder, has been lying free for at least 0.75 s, and was not
///    thrown into the drop area by the courier himself or taken back out of his trunk by a player (see below).
///    Bags with the least time left go first.
///  - Each one goes through the game's own Courier.ReceiveBag, after the same preparation as the host's
///    hand-over: the bag's PhotonView is taken over if another client owns it and the player's "working on
///    this order" claim is cleared. Everything after that is vanilla (claim broadcast, holder broadcast,
///    ParcelAdded broadcast, trunk visuals), so guests, modded or not, see exactly what they see when the
///    host hands a bag over. Loading stops at MaxOrderCount or when the courier stops being
///    ready; the mod checks the conditions ReceiveBag checks first, because a failed ReceiveBag would throw
///    the bag into the drop area.
///  - After the last load (1.2 s later, a short pause so the trunk state has reached everybody) the mod decides whether he leaves:
///    one of the orders in his trunk has under LeaveUnderSeconds left (per the countdown above), or no
///    active order is left unclaimed by a courier (OrderManager.GeneratedOrderLists, not expired, all in
///    CourierManager.m_ClaimedOrders), or the trunk is full. Then it calls
///    Courier.RequestDispatch(), the exact master-side effect of pressing E.
///  - A postfix on Courier.MoveBagToDropArea remembers the bags the courier himself put in the drop area (a
///    rejected hand-over, or a released parcel); they are not loaded again until someone moves them, so the
///    courier can't loop on a bag he just gave back. A postfix on Courier.GiveBackLastParcel (a player took the
///    last bag back out of the trunk, OnlineOrderInteraction.TryTakeBackFromCourier) remembers that bag with the
///    spot; it is not loaded again until it has been farther than ScanRadius from that spot (carried away), however
///    long it is held or put down near the scooter.
///
/// Multiplayer: the courier AI, the claim table and RequestDispatch are master-side, so this runs only on the
/// host (PhotonNetwork.IsConnectedAndSlave -> nothing). It makes no network calls of its own: it invokes the
/// game's methods, which broadcast. An unmodded guest therefore stays consistent. Bags a player holds (host
/// or guest) are IsHeld and never touched; a guest's bag counts as held from the moment the host handled
/// the guest's take request (Take_RPC sets the holder / parent). Residual race (as in vanilla between two
/// players): a guest's take is applied locally and only reaches the host about one round trip later, so a bag
/// grabbed at the very moment the courier loads it can end up in both places.
///
/// Not verified in-game (no way to run the game here); see CLAUDE.md for the test steps.
/// </summary>
internal static class AutoLoad
{
    private const float TickInterval = 0.4f;
    private const float SettleSeconds = 0.75f;
    private const float DepartDelay = 1.2f;          // stowing is instant (no tween); this is just a short pause
    private const float ScanWindow = 1.5f;  // how long a visit that has loaded nothing keeps looking after the courier is ready
    private const float IdleWatchInterval = 1.0f;   // how often an idle courier without a visit is checked for bags near him
    private const float GiveUpSeconds = 180f;       // a visit that never becomes ready (courier dismissed, ...)
    private const float DefaultDayEnd = 46800f;
    private const int FallbackTrunk = 3;
    private const float HeldSlack = 1.5f;           // extra metres a held taken-back bag must be carried away before it is forgotten

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
    /// <summary>Bags a player took back out of a trunk: instance id -> that courier's park spot. Ignored until the bag has been farther than ScanRadius from it.</summary>
    private static readonly Dictionary<int, Vector3> s_takenBack = new();
    /// <summary>Couriers (by pointer) that hold at least one bag this mod loaded and haven't been dispatched since; the idle watch gives them a visit again so the leave rules keep applying.</summary>
    private static readonly HashSet<IntPtr> s_modLoaded = new();
    private static float s_next;
    private static float s_nextIdleWatch;
    private static readonly Dictionary<string, float> s_errorLoggedAt = new();

    internal static void Reset()
    {
        s_visits.Clear();
        s_ignored.Clear();
        s_takenBack.Clear();
        s_modLoaded.Clear();
        s_nextIdleWatch = 0f;
        s_errorLoggedAt.Clear();
    }

    internal static void LogError(string where, Exception e)
    {
        // One line per source and minute, so a repeating failure doesn't flood the log but a different one still shows.
        float now = Time.realtimeSinceStartup;
        if (s_errorLoggedAt.TryGetValue(where, out var last) && now - last < 60f) return;
        s_errorLoggedAt[where] = now;
        Plugin.Logger.LogError($"[AutoCourier] {where} failed (at most one line per minute for this source): {e}");
    }

    private static void Log(string msg)
    {
        if (Plugin.VerboseLogging != null && Plugin.VerboseLogging.Value) Plugin.Logger.LogInfo($"[AutoCourier] {msg}");
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

    /// <summary>Courier.GiveBackLastParcel postfix: a player took this bag back out of the trunk; don't reload it while it stays near the spot.</summary>
    internal static void OnParcelGivenBack(CourierNpc courier, OrderPaperBag bag)
    {
        if (!Plugin.Active || bag == null || courier == null) return;
        if (s_takenBack.Count > 200) s_takenBack.Clear();
        s_takenBack[bag.GetInstanceID()] = ParkPoint(courier);
        Log($"a player took the bag back out of courier #{courier.EmployeeId}'s trunk; not loading it again until it has been carried away");
    }

    /// <summary>Forgets a taken-back bag once it has been farther than ScanRadius (HeldSlack more while it is held) from the spot it was taken from, or is gone.</summary>
    private static void ReleaseTakenBack()
    {
        if (s_takenBack.Count == 0) return;
        var bags = OnlineOrderPaperBagContainer.m_PaperBags;
        var alive = new HashSet<int>();
        float radius = Plugin.ScanRadius.Value;
        for (int i = 0; bags != null && i < bags.Count; i++)
        {
            var bag = bags[i];
            if (bag == null) continue;
            int id = bag.GetInstanceID();
            if (!s_takenBack.TryGetValue(id, out var park)) continue;
            alive.Add(id);
            // A held bag is at the player's hand, which can be outside a tiny radius while he stands at the trunk
            // (or far from a blocked park spot), so a held bag only counts as carried away with some slack.
            float away = Vector3.Distance(bag.transform.position, park);
            if (away > radius + (IsHeld(bag) ? HeldSlack : 0f)) s_takenBack.Remove(id);
        }
        var gone = new List<int>();
        foreach (var k in s_takenBack.Keys)
            if (!alive.Contains(k) && !HasBag(bags, k)) gone.Add(k);
        foreach (var k in gone) s_takenBack.Remove(k);
    }

    private static bool HasBag(Il2CppSystem.Collections.Generic.List<OrderPaperBag> bags, int id)
    {
        for (int i = 0; bags != null && i < bags.Count; i++)
            if (bags[i] != null && bags[i].GetInstanceID() == id) return true;
        return false;
    }

    // ---- per-frame work ---------------------------------------------------------------------------

    internal static void Tick()
    {
        if (!Plugin.Active) return;
        float now = Time.time;
        if (now < s_next) return;
        s_next = now + TickInterval;
        if (IsGuest) { s_visits.Clear(); s_takenBack.Clear(); return; }

        try { ReleaseTakenBack(); }
        catch (Exception e) { LogError("taken-back bookkeeping", e); }

        if (now >= s_nextIdleWatch)
        {
            s_nextIdleWatch = now + IdleWatchInterval;
            try { WatchIdleCouriers(now); }
            catch (Exception e) { LogError("idle watch", e); }
        }

        for (int i = s_visits.Count - 1; i >= 0; i--)
        {
            bool keep;
            try { keep = Step(s_visits[i], now); }
            catch (Exception e) { LogError("visit", e); keep = false; }
            if (!keep) s_visits.RemoveAt(i);
        }
    }

    /// <summary>
    /// A courier that is simply waiting at his parking space (he never "returned" since the game started, or his
    /// last visit ran out) has no visit. Start one as soon as a bag he could take lies near the spot.
    /// </summary>
    private static void WatchIdleCouriers(float now)
    {
        if (!NoktaSingleton<EmployeeManager>.HasInstance) return;
        var couriers = NoktaSingleton<EmployeeManager>.Instance.ActiveCouriers;
        if (couriers == null) return;
        for (int i = 0; i < couriers.Count; i++)
        {
            var c = couriers[i];
            if (c == null) continue;
            bool has = false;
            foreach (var v in s_visits)
                if (v.Ptr == c.Pointer) { has = true; break; }
            if (has) continue;

            // A courier still holding bags the mod loaded whose visit was dropped (short state switch, error): resume
            // watching so the leave rules (all covered, under a minute, full) still apply.
            if (s_modLoaded.Contains(c.Pointer))
            {
                if (c.IsDispatched || c.ParcelCount <= 0) { s_modLoaded.Remove(c.Pointer); continue; }
                if (IsIdle(c) && c.IsAvailableForHandOver)
                {
                    s_visits.Add(new Visit { Courier = c, Ptr = c.Pointer, ArmedAt = now, Loaded = 1, LastLoadAt = now - DepartDelay });
                    Log($"courier #{c.EmployeeId} still holds {c.ParcelCount} bag(s) the mod loaded; watching the leave rules again");
                }
                continue;
            }

            // Same readiness ReceiveBag demands.
            if (!IsIdle(c) || c.IsDispatched || !c.IsAvailableForHandOver) continue;
            if (c.ParcelCount >= Capacity(c) || c.IsFull) continue;
            if (!AnyCandidateNear(c)) continue;

            s_visits.Add(new Visit { Courier = c, Ptr = c.Pointer, ArmedAt = now });
            Log($"courier #{c.EmployeeId} is waiting at his parking space (trunk {c.ParcelCount}/{Capacity(c)}) " +
                $"and a bag lies within {Plugin.ScanRadius.Value:0.#} m; watching");
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
                if (c.IsDispatched) s_modLoaded.Remove(v.Ptr);
                Log($"courier #{c.EmployeeId} left or got busy; visit over ({v.Loaded} bag(s) loaded)");
                return false;
            }
            if (!c.IsAvailableForHandOver) return true;
        }

        // Even "0 = only when he arrives" lasts a moment, so that bags that were lying there before count as settled.
        // While he waits with something loaded, keep looking: the bag he is waiting for may arrive late.
        bool scanning = v.Scans == 0 || v.Loaded > 0 || now - v.ReadyAt <= ScanWindow;
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

    /// <summary>How many bags this courier may hold: his trunk limit.</summary>
    private static int Capacity(CourierNpc c)
    {
        int cap;
        try { cap = c.MaxOrderCount; }
        catch (Exception) { cap = FallbackTrunk; }
        if (cap < 1) cap = FallbackTrunk;
        return cap;
    }

    // ---- finding bags -----------------------------------------------------------------------------

    private struct Candidate
    {
        public OrderPaperBag Bag;
        public OrderListData Order;
        public float Distance;
        public float TimeLeft;
    }

    /// <summary>
    /// Whether a player holds the bag or it sits in a courier's hands or trunk. Not the game's
    /// OnlineOrderPaperBagContainer.IsCarried: its offline branch compares PlayerObjectHolder.m_CurrentObject, which
    /// OnlineOrderInteraction never clears when the bag leaves the hands (drop, throw, hand-over), so in a solo game
    /// the last bag the player dropped would count as carried until he picked up something else. Here: a parent
    /// transform (a held bag hangs under the player's object holder, a stowed one under the scooter), or online a
    /// non-empty NetworkOrderPaperBag holder, or the local player's OnlineOrderInteraction currently having it
    /// (m_CurrentOrderPaperBag is cleared by RemovePaperBag on drop and still covers placing mode, where the bag is
    /// unparented).
    /// </summary>
    private static bool IsHeld(OrderPaperBag bag)
    {
        if (bag.transform.parent != null) return true;
        if (PhotonNetwork.IsConnected)
        {
            var nb = bag.NetworkOrderPaperBag;
            if (nb != null && nb.HasHolder) return true;
        }
        if (NoktaSingleton<PlayerManager>.HasInstance)
        {
            var lp = NoktaSingleton<PlayerManager>.Instance.LocalPlayer;
            var ooi = lp != null ? lp.OnlineOrderInteraction : null;
            var cur = ooi != null ? ooi.CurrentOrderPaperBag : null;
            if (cur != null && cur.Pointer == bag.Pointer) return true;
        }
        return false;
    }

    private static Vector3 ParkPoint(CourierNpc c)
    {
        var t = c.ParkPosition;
        if (t == null) t = c.WaitingPosition;
        if (t == null) t = c.transform;
        return t.position;
    }

    /// <summary>
    /// Whether a bag could be loaded now apart from the settle time: nobody holds it, the courier didn't just put
    /// it back, it is within the radius, and the game's own hand-over check accepts it.
    /// </summary>
    private static bool IsEligible(OrderPaperBag bag, Vector3 park, float radius, out float dist, out OrderListData order)
    {
        dist = 0f;
        order = null;
        int id = bag.GetInstanceID();
        if (IsHeld(bag)) { s_ignored.Remove(id); return false; }
        if (s_ignored.TryGetValue(id, out var where))
        {
            if ((bag.transform.position - where).sqrMagnitude < 0.25f) return false; // still where the courier left it
            s_ignored.Remove(id);
        }
        if (s_takenBack.ContainsKey(id)) return false; // a player took it out of the trunk and is still near the spot with it
        dist = Vector3.Distance(bag.transform.position, park);
        if (dist > radius) return false;
        return CourierNpc.TryResolveHandOverOrder(bag, out order) && order != null;
    }

    private static bool AnyCandidateNear(CourierNpc c)
    {
        var bags = OnlineOrderPaperBagContainer.m_PaperBags;
        if (bags == null) return false;
        var park = ParkPoint(c);
        float radius = Plugin.ScanRadius.Value;
        for (int i = 0; i < bags.Count; i++)
        {
            var bag = bags[i];
            if (bag == null) continue;
            var go = bag.gameObject;
            if (go == null || !go.activeInHierarchy) continue;
            if (IsEligible(bag, park, radius, out _, out _)) return true;
        }
        return false;
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

            float dist;
            OrderListData order;
            if (!IsEligible(bag, park, radius, out dist, out order)) continue;

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
            if (IsHeld(cand.Bag)) continue; // picked up since the scan
            if (Load(c, cand, cap))
            {
                v.Loaded++;
                s_modLoaded.Add(v.Ptr);
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
        float threshold = Plugin.LeaveUnderSeconds;
        var parcels = c.m_Parcels;
        for (int i = 0; parcels != null && i < parcels.Count; i++)
        {
            var order = parcels[i]?.Order;
            if (order == null) continue;
            float left = TimeLeft(order);
            if (left < threshold) { reason = $"order #{order.ID} has only {Math.Max(left, 0f):0} s left"; break; }
        }
        if (reason == null && NoOrderWaiting())
            reason = "his trunk covers every active order";
        if (reason == null && (c.IsFull || c.ParcelCount >= Capacity(c)))
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

[HarmonyPatch(typeof(CourierNpc), nameof(CourierNpc.GiveBackLastParcel))]
internal static class AutoCourier_GiveBackLastParcel_Patch
{
    private static void Postfix(CourierNpc __instance, OrderPaperBag __result)
    {
        try { AutoLoad.OnParcelGivenBack(__instance, __result); }
        catch (Exception e) { AutoLoad.LogError("GiveBackLastParcel postfix", e); }
    }
}
