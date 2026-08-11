Imports Current.PluginApi

' =============================================================================
' FILE: ClickOwnership.vb   (MenuSystem project)
' Answers ONE question about the input event now being dispatched:
'   which spatial zone owns it?
' =============================================================================
'
' THE FAULT THIS CLOSES. The aggregator publishes each click TWICE - first the
' generic event carrying the host's occlusion-AWARE cell pick, then a zone tap
' chosen by an occlusion-BLIND ray walk. Anything drawn between eye and wall
' splits the two: a menu is asked by ray and arms its tool, whilst every cell test
' answers "not mine" and the click falls through to the drawing session, which
' clears the selection and reports "Select". Two owners, one press, no error.
'
' THE ANSWER IS COMPUTED ONCE AND KEYED ON THE PAYLOAD REFERENCE. Publish hands
' every subscriber the same object, so that reference IS the event's identity. No
' clock, no sequence number, no staleness window, no cache invalidation.
'
' WE SUBSCRIBE FIRST, at the head of MenuSystemPlugin.Execute - ahead of
' ContextMenuManager, every MenuInstance, and every plugin that borrows pooled
' zones (none may start before MenuSystem has registered). So the verdict is taken
' from the snapshot of spatial zones as they existed before any handler could park a zone.
'
' NOTHING IS ASKED OF THE CALLER. Park and raise zones freely from your click
' handler - SwitchZoneToMarginSetA, SwitchZoneToMarginSetB, SwapZoneMarginSets,
' MarginJump, ReleaseZone, in any order. Compute tests a SNAPSHOT taken before
' your handler ran, never the live set of spatial zones, so your parking cannot alter the answer
' and nobody behind you loses anything.
'
' THIS COULD NOT HAVE BEEN AUTOMATED BY WRAPPING THOSE COMMANDS. Four of the five
' are HOST verbs on ICurrentApi called directly by plugins; only ReleaseZone is
' menu-published. Therefore we take the snapshot-based responsibility here rather
' than trying to intercept every caller (this is a pragmatic convention, not an enforced global policy).
'
' GEOMETRY: occlusion-blind ray against zone AABBs - the aggregator's own
' arithmetic, NOT the host's cell pick and NOT GetGazeRay. We must predict the
' router, so we must ask in the router's words; a more accurate ray disagrees at a
' box edge and reopens the split in the other direction.
'
' NAMING. This type is Friend (internal) and its members are intentionally named
' differently from the public MenuSystemPlugin methods so plugin developers see a
' single public API surface without near-duplicate internal names.
Friend Class ClickOwnershipResolver

    Private ReadOnly _api As ICurrentApi
    Private ReadOnly _aggregator As Object
    Private ReadOnly _sync As New Object()

    ' The snapshot of spatial zones, frozen. Compute tests THIS. Built whole into a local and published
    ' by ONE reference assignment, which is atomic - so a reader takes it into a
    ' local and can never see a torn set. That is why Snap needs no lock and must
    ' not take one: it walks every zone, and holding _sync for that would block the
    ' click thread behind the poll thread.
    Private NotInheritable Class ZoneBox
        Public ReadOnly Id As String
        Public ReadOnly MinX, MinY, MinZ, MaxX, MaxY, MaxZ As Integer
        Public Sub New(id As String, bb As ((Integer, Integer, Integer), (Integer, Integer, Integer)))
            Me.Id = id
            MinX = bb.Item1.Item1 : MinY = bb.Item1.Item2 : MinZ = bb.Item1.Item3
            MaxX = bb.Item2.Item1 : MaxY = bb.Item2.Item2 : MaxZ = bb.Item2.Item3
        End Sub
    End Class

    Private _snapshot As ZoneBox() = New ZoneBox() {}

    ' The stamp. Keeping a reference in _evt prevents the payload from being garbage-collected or reallocated,
    ' so ReferenceEquals reliably identifies the same event object.
    Private _evt As Object = Nothing
    Private _owner As String = ""
    Private _dist As Double = -1.0R
    Private _seen As Integer = 0
    Private _lateResolves As Long = 0

    Private _onClick As Action(Of Object)
    Private _onCross As Action(Of Object)

    Public Sub New(api As ICurrentApi, aggregator As Object)
        _api = api
        _aggregator = aggregator
    End Sub

    ' Called at the TOP of Execute. This must run before other initialization
    ' so the resolver can capture pre-dispatch zone state used for ownership decisions.
    ' The initial snapshot captures whatever spatial zones exist at startup (the pool
    ' may not yet exist and other plugins may have registered zones). This is harmless:
    ' Stamp rebuilds the snapshot on the first click, and only late resolves might read
    ' that initial snapshot and therefore see a stale view.
    Friend Sub BeginArbitration()
        Snap()
        If _aggregator Is Nothing Then
            Console.WriteLine("[ClickOwnership] No aggregator - lazy resolution only.")
            Return
        End If
        _onClick = Sub(evt) Stamp(evt)
        _onCross = Sub(evt) Snap()
        Try
            CallByName(_aggregator, "Subscribe", CallType.Method, "MouseClickLeft", _onClick)
            CallByName(_aggregator, "Subscribe", CallType.Method, "MouseClickRight", _onClick)
            ' Zone crossings arrive on the aggregator's own poll thread, NEVER inside a
            ' click, so they refresh the snapshot at a safe moment. And a man must look
            ' at a zone before he can click it, so aiming keeps the snapshot of spatial zones current.
            CallByName(_aggregator, "Subscribe", CallType.Method, "SpatialZoneMouseEnter", _onCross)
            CallByName(_aggregator, "Subscribe", CallType.Method, "SpatialZoneMouseLeave", _onCross)
            Console.WriteLine("[ClickOwnership] Subscribed FIRST to clicks and zone crossings.")
        Catch ex As Exception
            Console.WriteLine($"[ClickOwnership] Subscribe failed: {ex.Message}")
        End Try
    End Sub

    ' ═══════════════ THE INTERNAL SURFACE ═════════════════════════════════════
    ' Pass the payload you were handed. Never synthesise one: the reference is the
    ' cache key, and a fresh object forces a recompute against the live set of spatial
    ' zones that your handler may already have altered.

    ' Zone id of the NEAREST raised zone the click's ray passes through, else "".
    Friend Function ZoneIdOwningClick(evt As Object) As String
        If evt Is Nothing Then Return ""
        SyncLock _sync
            If Object.ReferenceEquals(evt, _evt) Then Return _owner
            ' No stamp for this event: something subscribed to the click ahead of the
            ' menu system. We deliberately do NOT walk the zones now - a handler that
            ' ran before us may already have parked one. Answer from the last snapshot
            ' taken outside dispatch, and say so, loudly and once per occurrence.
            _lateResolves += 1
            Console.WriteLine("[ClickOwnership] LATE resolve #" & _lateResolves &
                              " - event never stamped. Something subscribed to the " &
                              "click ahead of MenuSystem; answering from the last " &
                              "pre-dispatch snapshot.")
            Compute(evt)
            Return _owner
        End SyncLock
    End Function

    ' Diagnostic. The fault class this file closes was INVISIBLE - two claimants, no
    ' exception, one wrong prompt - so a disagreement must be one call away.
    ' Keys: owner, distance, zonesTested, fresh, lateResolves, worldSize.
    ' Does NOT tick _lateResolves: an inspector asking about an old event is not the
    ' ordering fault that counter exists to expose.
    Friend Function OwnershipDiagnostics(evt As Object) As Dictionary(Of String, Object)
        Dim d As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
        SyncLock _sync
            Dim fresh = Object.ReferenceEquals(evt, _evt)
            If Not fresh AndAlso evt IsNot Nothing Then Compute(evt)
            d("owner") = _owner
            d("distance") = _dist
            d("zonesTested") = _seen
            d("fresh") = fresh
            d("lateResolves") = _lateResolves
            d("worldSize") = _snapshot.Length
        End SyncLock
        Return d
    End Function

    ' ═══════════════ INTERNALS ════════════════════════════════════════════════

    Private Sub Stamp(evt As Object)
        SyncLock _sync
            If Object.ReferenceEquals(evt, _evt) Then Return
            Snap()          ' we are first; the snapshot reflects zone state as it was before any handler ran
            Compute(evt)
        End SyncLock
    End Sub

    ' A zone parked in slot A carries all four margins at zero and so has no area;
    ' that degenerate rectangle IS the "not raised" test, and it wants no register of
    ' who owns what. If Snap() throws while building a new snapshot, the previous snapshot is
    ' intentionally preserved so ownership decisions continue using the last known consistent data.
    Private Sub Snap()
        Try
            Dim o As New List(Of ZoneBox)
            For Each z In _api.GetAllSpatialZones()
                If z Is Nothing Then Continue For
                If z.Right <= z.Left OrElse z.Bottom <= z.Top Then Continue For
                o.Add(New ZoneBox(z.ID, z.BoundingBoxAABB))
            Next
            _snapshot = o.ToArray()
        Catch ex As Exception
            Console.WriteLine($"[ClickOwnership] Snapshot failed: {ex.Message}")
        End Try
    End Sub

    ' Under _sync. TESTS _snapshot, NEVER A LIVE WALK - that is the whole of why a
    ' plugin may park zones from its click handler without robbing anyone.
    ' NEAREST hit, not first found: two zones on one ray is a real arrangement, and
    ' the near one is what the man is looking at.
    ' A degenerate (zero-length) ray returns no owner (""), matching the aggregator which
    ' treats a zero vector as no tap and routes nothing.
    Private Sub Compute(evt As Object)
        _evt = evt
        _owner = ""
        _dist = -1.0R
        _seen = 0
        Try
            Dim o = Read(evt, "ObserverOrigin")
            Dim u = Read(evt, "ObserverUnitVector")
            If o Is Nothing OrElse u Is Nothing Then Return
            Dim dx = Num(u, "Item1"), dy = Num(u, "Item2"), dz = Num(u, "Item3")
            If dx = 0 AndAlso dy = 0 AndAlso dz = 0 Then Return
            Dim ox = Num(o, "Item1"), oy = Num(o, "Item2"), oz = Num(o, "Item3")

            Dim world = _snapshot          ' one read; the array cannot change under us
            Dim best As Double = Double.MaxValue
            For i = 0 To world.Length - 1
                Dim b = world(i)
                _seen += 1
                Dim t = Entry(ox, oy, oz, dx, dy, dz, b)
                If t >= 0 AndAlso t < best Then
                    best = t
                    _owner = b.Id
                End If
            Next
            If _owner <> "" Then _dist = best
        Catch ex As Exception
            _owner = ""
            Console.WriteLine($"[ClickOwnership] Resolve failed: {ex.Message}")
        End Try
    End Sub

    ' Entry distance along the ray, or -1 for a miss. This slab intersection test must
    ' match the aggregator's EventAggregator.RayIntersectsZoneAabb implementation exactly
    ' at box edges; if they disagree, ownership decisions and routing can diverge.
    Private Shared Function Entry(ox As Double, oy As Double, oz As Double,
                                  dx As Double, dy As Double, dz As Double,
                                  b As ZoneBox) As Double
        Dim tMin As Double = 0.0R
        Dim tMax As Double = Double.PositiveInfinity
        If Not Slab(ox, dx, b.MinX, b.MaxX, tMin, tMax) Then Return -1.0R
        If Not Slab(oy, dy, b.MinY, b.MaxY, tMin, tMax) Then Return -1.0R
        If Not Slab(oz, dz, b.MinZ, b.MaxZ, tMin, tMax) Then Return -1.0R
        If tMax < 0 Then Return -1.0R
        Return Math.Max(tMin, 0.0R)
    End Function

    Private Shared Function Slab(o As Double, d As Double, lo As Integer, hi As Integer,
                                 ByRef tMin As Double, ByRef tMax As Double) As Boolean
        If Math.Abs(d) < Double.Epsilon Then Return o >= lo AndAlso o <= hi
        Dim inv = 1.0R / d
        Dim t1 = (lo - o) * inv
        Dim t2 = (hi - o) * inv
        If t1 > t2 Then
            Dim s = t1 : t1 = t2 : t2 = s
        End If
        If t1 > tMin Then tMin = t1
        If t2 < tMax Then tMax = t2
        Return tMax >= tMin
    End Function

    ' The payload is an anonymous type from the aggregator's assembly, so it is read
    ' late-bound - the same door MenuInstance.SafeGetString already uses. ValueTuple
    ' members are public FIELDS and CallByName reads those too, so this holds whatever
    ' tuple element types the aggregator publishes.
    Private Shared Function Read(obj As Object, name As String) As Object
        If obj Is Nothing Then Return Nothing
        Try : Return CallByName(obj, name, CallType.Get) : Catch : Return Nothing : End Try
    End Function

    ' CDbl, never a ToString round-trip, which breaks silently under comma-decimal.
    Private Shared Function Num(tup As Object, field As String) As Double
        Dim v = Read(tup, field)
        If v Is Nothing Then Return 0
        Try : Return CDbl(v) : Catch : Return 0 : End Try
    End Function

End Class