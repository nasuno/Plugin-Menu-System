'ContextMenuManager.vb
Imports System.Collections.Concurrent
Imports System.Threading
Imports Current.PluginApi

Public Class ContextMenuManager

    Private Const CharWidth As Integer = 5
    Private Const CharHeight As Integer = 7
    Private Const Gutter As Integer = 1

    Private Shared ReadOnly ZoneHeightBase As Integer = CharHeight + 2 * Gutter + 1

    Public Shared Function GetZoneHeight() As Integer
        Return ZoneHeightBase
    End Function

    Public Shared Function CalculateZoneWidth(charCount As Integer) As Integer
        Return charCount * CharWidth + (charCount + 1) * Gutter + 1
    End Function

    Private ReadOnly _api As ICurrentApi
    Private ReadOnly _aggregator As Object
    Private ReadOnly _onItemSelected As Action(Of String, String, Integer, String)
    Private ReadOnly _menuSystem As MenuSystemPlugin

    ' Submenu definitions
    Private ReadOnly _submenuDefs As New Dictionary(Of String, String())()

    ' Active main menu state
    Private _mainItems As New List(Of ContextMenuItem)()
    Private _mainZoneIds As New HashSet(Of String)()
    Private _isVisible As Boolean = False
    Private _currentPanel As PanelType
    Private _currentCharWidth As Integer
    Private _mainTop As Integer
    Private _mainBottom As Integer
    Private _mainLeft As Integer
    Private _mainRight As Integer

    ' Active submenu state
    Private _subMenuStack As New List(Of SubMenuState)()

    ' Pinned submenu tracking
    Private _pinnedItem As ContextMenuItem = Nothing
    Private _pinnedDepth As Integer = -1

    ' State flags
    Private _inSelectionFeedback As Boolean = False
    Private _justShownThisEvent As Boolean = False
    Private _collapseCheckPending As Boolean = False

    ' Overlap collapse tracking per level
    Private ReadOnly _collapsedLevels As New Dictionary(Of String, Boolean)()

    ' Hover tracking
    Private ReadOnly _hoveredZones As New ConcurrentDictionary(Of String, Boolean)()

    ' Event callbacks
    Private _zoneEnterCallback As Action(Of Object)
    Private _zoneLeaveCallback As Action(Of Object)
    Private _zoneClickLeftCallback As Action(Of Object)
    Private _globalLeftClickCallback As Action(Of Object)
    Private _globalRightClickCallback As Action(Of Object)

    Private _dropdownCollapseCallback As Action = Nothing

    Public Sub New(api As ICurrentApi, aggregator As Object,
                   onItemSelected As Action(Of String, String, Integer, String),
                   menuSystem As MenuSystemPlugin)
        _api = api
        _aggregator = aggregator
        _onItemSelected = onItemSelected
        _menuSystem = menuSystem
    End Sub

    Public Sub Initialize()
        SubscribeToEvents()
        Console.WriteLine("[ContextMenu] Initialized.")
    End Sub

    ' ====================
    ' == PUBLIC API FOR MENUINSTANCE COORDINATION
    ' ====================

    Public Sub SetPinnedSubmenu(parentItemName As String, depth As Integer)
        If _subMenuStack.Any(Function(s) s.Depth = depth) Then
            _pinnedItem = New ContextMenuItem("", parentItemName, -1, True, depth)
            _pinnedDepth = depth
            Console.WriteLine($"[ContextMenu] External pin set: '{parentItemName}' at depth {depth}")
        End If
    End Sub

    Public Sub ClearPinnedSubmenu()
        _pinnedItem = Nothing
        _pinnedDepth = -1
        Console.WriteLine("[ContextMenu] External pin cleared")
    End Sub

    ' ====================
    ' == EVENT SUBSCRIPTIONS
    ' ====================

    Private Sub SubscribeToEvents()
        If _aggregator Is Nothing Then Return

        _zoneEnterCallback = Sub(evt) OnZoneEnter(SafeGetString(evt, "ZoneId"))
        _zoneLeaveCallback = Sub(evt) OnZoneLeave(SafeGetString(evt, "ZoneId"))
        _zoneClickLeftCallback = Sub(evt) OnZoneClickLeft(SafeGetString(evt, "ZoneId"))
        _globalLeftClickCallback = Sub(evt) OnGlobalLeftClick(evt)
        _globalRightClickCallback = Sub(evt) OnGlobalRightClick()

        Try
            CallByName(_aggregator, "Subscribe", CallType.Method, "SpatialZoneMouseEnter", _zoneEnterCallback)
            CallByName(_aggregator, "Subscribe", CallType.Method, "SpatialZoneMouseLeave", _zoneLeaveCallback)
            CallByName(_aggregator, "Subscribe", CallType.Method, "SpatialZoneMouseClickLeft", _zoneClickLeftCallback)
            CallByName(_aggregator, "Subscribe", CallType.Method, "MouseClickLeft", _globalLeftClickCallback)
            CallByName(_aggregator, "Subscribe", CallType.Method, "MouseClickRight", _globalRightClickCallback)
        Catch ex As Exception
            Console.WriteLine($"[ContextMenu] Subscribe failed: {ex.Message}")
        End Try
    End Sub

    ' ====================
    ' == SUBMENU DEFINITIONS
    ' ====================

    Public Sub DefineSubmenu(parentItemName As String, childItems() As String)
        If String.IsNullOrWhiteSpace(parentItemName) OrElse childItems Is Nothing Then Return
        _submenuDefs(parentItemName) = childItems
    End Sub

    Public Sub ClearSubmenuDefinitions()
        _submenuDefs.Clear()
    End Sub

    Public Function HasSubmenu(itemName As String) As Boolean
        Return _submenuDefs.ContainsKey(itemName)
    End Function

    Public Function IsSubmenuActiveAndHovered() As Boolean
        If _subMenuStack.Count = 0 Then Return False
        For Each state In _subMenuStack
            For Each item In state.Items
                Dim isHovered As Boolean = False
                If _hoveredZones.TryGetValue(item.ZoneId, isHovered) AndAlso isHovered Then Return True
            Next
        Next
        Return False
    End Function

    ' Returns true for any zone this manager currently controls (main or submenu).
    ' This is public so MenuInstance and MenuSystemPlugin can query whether a zone belongs to the context menu.
    Friend Function ContextOwnsZone(zoneId As String) As Boolean
        If zoneId = "" Then Return False
        If _mainZoneIds.Contains(zoneId) Then Return True
        For Each state In _subMenuStack
            If state.ZoneIds.Contains(zoneId) Then Return True
        Next
        Return False
    End Function

    Public Sub HideAllSubmenus()
        _dropdownCollapseCallback = Nothing
        _pinnedItem = Nothing
        _pinnedDepth = -1
        RevertAllSubmenuHeaderTexts()
        HideSubmenusAtDepth(0)
    End Sub

    Public Sub HideAllSubmenusWithFeedback()
        _dropdownCollapseCallback = Nothing
        _pinnedItem = Nothing
        _pinnedDepth = -1
        If _subMenuStack.Count = 0 Then Return
        For Each state In _subMenuStack
            For Each item In state.Items
                HideAndReleaseZone(item.ZoneId)
            Next
        Next
        _subMenuStack.Clear()
    End Sub

    Public Function IsInSelectionFeedback() As Boolean
        Return _inSelectionFeedback
    End Function

    ' ====================
    ' == AUTO-WIDTH CALCULATION
    ' ====================

    Private Function CalculateAutoWidth(items() As String) As Integer
        Dim maxLen = 0
        Dim anyHasSubmenu = False

        For Each itemName In items
            If itemName.Length > maxLen Then maxLen = itemName.Length
            If _submenuDefs.ContainsKey(itemName) Then anyHasSubmenu = True
        Next

        If anyHasSubmenu Then maxLen += MenuSystemPlugin.SubmenuPrefixWidth
        Return maxLen
    End Function

    Private Function CalculateSubmenuAutoWidth(childItems() As String) As Integer
        Dim maxLen = 0
        Dim anyHasSubmenu = False

        For Each itemName In childItems
            If itemName.Length > maxLen Then maxLen = itemName.Length
            If _submenuDefs.ContainsKey(itemName) Then anyHasSubmenu = True
        Next

        If anyHasSubmenu Then maxLen += MenuSystemPlugin.SubmenuPrefixWidth
        Return maxLen
    End Function

    ' ====================
    ' == BOUNDING BOX CALCULATION
    ' ====================

    Private Function CalculateMainMenuBounds() As ((Integer, Integer, Integer), (Integer, Integer, Integer))
        Dim minX = Integer.MaxValue, minY = Integer.MaxValue, minZ = Integer.MaxValue
        Dim maxX = Integer.MinValue, maxY = Integer.MinValue, maxZ = Integer.MinValue

        For Each item In _mainItems
            Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
            If zone IsNot Nothing Then
                Dim bb = zone.BoundingBoxAABB
                UpdateMinMax(bb, minX, minY, minZ, maxX, maxY, maxZ)
            End If
        Next

        If minX = Integer.MaxValue Then Return ((0, 0, 0), (0, 0, 0))
        Return ((minX, minY, minZ), (maxX, maxY, maxZ))
    End Function

    Private Function CalculateSubmenuBounds(state As SubMenuState) As ((Integer, Integer, Integer), (Integer, Integer, Integer))
        Dim minX = Integer.MaxValue, minY = Integer.MaxValue, minZ = Integer.MaxValue
        Dim maxX = Integer.MinValue, maxY = Integer.MinValue, maxZ = Integer.MinValue

        For Each item In state.Items
            Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
            If zone IsNot Nothing Then
                Dim bb = zone.BoundingBoxAABB
                UpdateMinMax(bb, minX, minY, minZ, maxX, maxY, maxZ)
            End If
        Next

        If minX = Integer.MaxValue Then Return ((0, 0, 0), (0, 0, 0))
        Return ((minX, minY, minZ), (maxX, maxY, maxZ))
    End Function

    Private Sub UpdateMinMax(bb As ((Integer, Integer, Integer), (Integer, Integer, Integer)),
                          ByRef minX As Integer, ByRef minY As Integer, ByRef minZ As Integer,
                          ByRef maxX As Integer, ByRef maxY As Integer, ByRef maxZ As Integer)
        If bb.Item1.Item1 < minX Then minX = bb.Item1.Item1
        If bb.Item1.Item2 < minY Then minY = bb.Item1.Item2
        If bb.Item1.Item3 < minZ Then minZ = bb.Item1.Item3
        If bb.Item2.Item1 > maxX Then maxX = bb.Item2.Item1
        If bb.Item2.Item2 > maxY Then maxY = bb.Item2.Item2
        If bb.Item2.Item3 > maxZ Then maxZ = bb.Item2.Item3
    End Sub

    Private Function GetMainMenuId() As String
        Return "__ContextMenu_Main__"
    End Function

    Private Function GetSubmenuId(depth As Integer) As String
        Return $"__ContextMenu_Sub_{depth}__"
    End Function

    ' ====================
    ' == OVERLAP CALLBACKS
    ' ====================

    Private Sub CollapseMainMenu()
        Dim menuId = GetMainMenuId()
        If _collapsedLevels.ContainsKey(menuId) AndAlso _collapsedLevels(menuId) Then Return
        _collapsedLevels(menuId) = True

        For Each item In _mainItems
            Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
            UnregisterZone(zone)
            _api.SwitchZoneToMarginSetA(item.ZoneId)
        Next
        Console.WriteLine("[ContextMenu] Main menu collapsed due to overlap")
    End Sub

    Private Sub RestoreMainMenu()
        Dim menuId = GetMainMenuId()
        If Not _collapsedLevels.ContainsKey(menuId) OrElse Not _collapsedLevels(menuId) Then Return
        _collapsedLevels(menuId) = False

        For Each item In _mainItems
            _api.SwitchZoneToMarginSetB(item.ZoneId)
            RegisterZone(_menuSystem.GetPooledZoneRef(item.ZoneId))
        Next
        Console.WriteLine("[ContextMenu] Main menu restored")
    End Sub

    Private Function CreateSubmenuCollapseAction(depth As Integer) As Action
        Return Sub()
                   Dim menuId = GetSubmenuId(depth)
                   If _collapsedLevels.ContainsKey(menuId) AndAlso _collapsedLevels(menuId) Then Return
                   _collapsedLevels(menuId) = True

                   Dim state = _subMenuStack.FirstOrDefault(Function(s) s.Depth = depth)
                   If state Is Nothing Then Return

                   For Each item In state.Items
                       Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
                       UnregisterZone(zone)
                       _api.SwitchZoneToMarginSetA(item.ZoneId)
                   Next
                   Console.WriteLine($"[ContextMenu] Submenu depth {depth} collapsed due to overlap")
               End Sub
    End Function

    Private Function CreateSubmenuRestoreAction(depth As Integer) As Action
        Return Sub()
                   Dim menuId = GetSubmenuId(depth)
                   If Not _collapsedLevels.ContainsKey(menuId) OrElse Not _collapsedLevels(menuId) Then Return
                   _collapsedLevels(menuId) = False

                   Dim state = _subMenuStack.FirstOrDefault(Function(s) s.Depth = depth)
                   If state Is Nothing Then Return

                   For Each item In state.Items
                       _api.SwitchZoneToMarginSetB(item.ZoneId)
                       RegisterZone(_menuSystem.GetPooledZoneRef(item.ZoneId))
                   Next
                   Console.WriteLine($"[ContextMenu] Submenu depth {depth} restored")
               End Sub
    End Function

    Private Function IsLevelCollapsed(menuId As String) As Boolean
        Return _collapsedLevels.ContainsKey(menuId) AndAlso _collapsedLevels(menuId)
    End Function

    ' ====================
    ' == SHOW MAIN CONTEXT MENU
    ' ====================

    Public Sub Show(panel As PanelType, clickRow As Integer, clickCol As Integer,
                items() As String, charWidth As Integer, autoWidth As Boolean)
        If items Is Nothing OrElse items.Length = 0 Then Return
        If _isVisible Then HideAll()

        _currentPanel = panel
        _inSelectionFeedback = False
        _justShownThisEvent = True
        _pinnedItem = Nothing
        _pinnedDepth = -1
        _collapsedLevels.Clear()

        _currentCharWidth = If(autoWidth, CalculateAutoWidth(items), charWidth)
        Dim zoneWidth = CalculateZoneWidth(_currentCharWidth)

        Dim pL = _api.GetPanelFurthestLeftColumn(panel)
        Dim pR = _api.GetPanelFurthestRightColumn(panel)
        Dim pT = _api.GetPanelFurthestTopRow(panel)
        Dim pB = _api.GetPanelFurthestBottomRow(panel)

        Dim totalH = items.Length * ZoneHeightBase
        Dim anchorRow = clickRow
        Dim anchorCol = clickCol

        If anchorRow - totalH < pT Then
            anchorRow = pT + totalH
            If anchorRow > pB Then anchorRow = pB
        End If

        If anchorCol + zoneWidth > pR Then
            anchorCol = pR - zoneWidth
            If anchorCol < pL Then anchorCol = pL
        End If

        _mainTop = anchorRow - totalH
        _mainBottom = anchorRow
        _mainLeft = anchorCol
        _mainRight = anchorCol + zoneWidth

        For i As Integer = 0 To items.Length - 1
            Dim zoneId = _menuSystem.AcquireZone()
            Dim itemName = items(i)
            Dim hasSub = _submenuDefs.ContainsKey(itemName)
            Dim displayText = If(hasSub, "* " & itemName, itemName)

            Dim itemTop = anchorRow - ((i + 1) * ZoneHeightBase)
            Dim itemBottom = anchorRow - (i * ZoneHeightBase)

            PositionAndShowZone(zoneId, panel, itemTop, itemBottom, anchorCol, anchorCol + zoneWidth, displayText)

            _mainItems.Add(New ContextMenuItem(zoneId, itemName, i, hasSub, 0))
            _mainZoneIds.Add(zoneId)
        Next

        _isVisible = True

        If _menuSystem IsNot Nothing Then
            Dim bounds = CalculateMainMenuBounds()
            Dim menuId = GetMainMenuId()
            _menuSystem.RegisterActiveMenu(menuId, bounds, AddressOf CollapseMainMenu, AddressOf RestoreMainMenu)
            _menuSystem.NotifyMenuShown(menuId)
        End If

        Console.WriteLine($"[ContextMenu] Shown: {items.Length} items, width={_currentCharWidth}")
    End Sub

    ' ====================
    ' == SHOW SUBMENU
    ' ====================

    Public Sub ShowSubmenu(parentItemName As String, parentPanel As PanelType,
                       parentTop As Integer, parentBottom As Integer,
                       parentLeft As Integer, parentRight As Integer,
                       depth As Integer, menuSource As String,
                       Optional dropdownCollapseCallback As Action = Nothing)
        If Not _submenuDefs.ContainsKey(parentItemName) Then Return
        Dim childItems = _submenuDefs(parentItemName)
        If childItems.Length = 0 Then Return

        If Not _isVisible Then
            _isVisible = True
        End If

        If dropdownCollapseCallback IsNot Nothing Then _dropdownCollapseCallback = dropdownCollapseCallback

        HideSubmenusAtDepth(depth)

        Dim subCharWidth = CalculateSubmenuAutoWidth(childItems)
        If Not MenuSystemPlugin.DefaultContextAutoWidth Then
            subCharWidth = Math.Max(subCharWidth, _currentCharWidth)
        End If
        Dim subZoneWidth = CalculateZoneWidth(subCharWidth)

        Dim pL = _api.GetPanelFurthestLeftColumn(parentPanel)
        Dim pR = _api.GetPanelFurthestRightColumn(parentPanel)
        Dim pT = _api.GetPanelFurthestTopRow(parentPanel)
        Dim pB = _api.GetPanelFurthestBottomRow(parentPanel)

        Dim totalH = childItems.Length * ZoneHeightBase
        Dim subLeft = parentRight + 1
        Dim subRight = subLeft + subZoneWidth
        Dim subTop = parentTop

        If subRight > pR Then
            subRight = parentLeft - 1
            subLeft = subRight - subZoneWidth
            If subLeft < pL Then subLeft = pL
        End If

        If subTop + totalH > pB Then
            subTop = pB - totalH
            If subTop < pT Then subTop = pT
        End If

        Dim state As New SubMenuState() With {
            .Depth = depth,
            .ParentItemName = parentItemName,
            .MenuSource = menuSource,
            .Top = subTop,
            .Bottom = subTop + totalH,
            .Left = subLeft,
            .Right = subRight,
            .CharWidth = subCharWidth
        }

        For i As Integer = 0 To childItems.Length - 1
            Dim zoneId = _menuSystem.AcquireZone()
            Dim itemName = childItems(i)
            Dim hasSub = _submenuDefs.ContainsKey(itemName)
            Dim displayText = If(hasSub, "* " & itemName, itemName)

            Dim itemTop = subTop + (i * ZoneHeightBase)
            Dim itemBottom = subTop + ((i + 1) * ZoneHeightBase)

            PositionAndShowZone(zoneId, parentPanel, itemTop, itemBottom, subLeft, subRight, displayText)

            Dim menuItem = New ContextMenuItem(zoneId, itemName, i, hasSub, depth)
            state.Items.Add(menuItem)
            state.ZoneIds.Add(zoneId)
        Next

        _subMenuStack.Add(state)

        If _menuSystem IsNot Nothing Then
            Dim bounds = CalculateSubmenuBounds(state)
            Dim menuId = GetSubmenuId(depth)
            _menuSystem.RegisterActiveMenu(menuId, bounds, CreateSubmenuCollapseAction(depth), CreateSubmenuRestoreAction(depth))
            _menuSystem.NotifyMenuShown(menuId)
        End If

        Console.WriteLine($"[ContextMenu] Submenu depth {depth}: {childItems.Length} items, width={subCharWidth}")
    End Sub

    Private Sub HideSubmenusAtDepth(depth As Integer)
        Dim toRemove = _subMenuStack.Where(Function(s) s.Depth >= depth).ToList()
        For Each state In toRemove
            Dim menuId = GetSubmenuId(state.Depth)
            _menuSystem?.UnregisterActiveMenu(menuId)
            _collapsedLevels.Remove(menuId)

            RevertParentItemText(state.ParentItemName, state.Depth)
            For Each item In state.Items
                HideAndReleaseZone(item.ZoneId)
            Next
            _subMenuStack.Remove(state)
        Next
    End Sub

    ' ====================
    ' == TEXT UPDATE HELPERS
    ' ====================

    Private Sub UpdateItemTextToActive(item As ContextMenuItem)
        If item Is Nothing OrElse Not item.HasSubmenu Then Return
        Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
        If zone IsNot Nothing Then zone.Text = "X " & item.Name
    End Sub

    Private Sub RevertItemTextToInactive(item As ContextMenuItem)
        If item Is Nothing OrElse Not item.HasSubmenu Then Return
        Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
        If zone IsNot Nothing Then zone.Text = "* " & item.Name
    End Sub

    Private Sub RevertParentItemText(parentName As String, depth As Integer)
        If depth = 0 Then
            Dim mainItem = _mainItems.FirstOrDefault(Function(i) i.Name = parentName)
            If mainItem IsNot Nothing Then RevertItemTextToInactive(mainItem)
        Else
            Dim parentState = _subMenuStack.FirstOrDefault(Function(s) s.Depth = depth - 1)
            If parentState IsNot Nothing Then
                Dim parentItem = parentState.Items.FirstOrDefault(Function(i) i.Name = parentName)
                If parentItem IsNot Nothing Then RevertItemTextToInactive(parentItem)
            End If
        End If
    End Sub

    Private Sub RevertAllSubmenuHeaderTexts()
        For Each item In _mainItems
            If item.HasSubmenu Then RevertItemTextToInactive(item)
        Next
        For Each state In _subMenuStack
            For Each item In state.Items
                If item.HasSubmenu Then RevertItemTextToInactive(item)
            Next
        Next
    End Sub

    ' ====================
    ' == ZONE HELPERS
    ' ====================

    Private Sub PositionAndShowZone(zoneId As String, panel As PanelType,
                                 top As Integer, bottom As Integer,
                                 left As Integer, right As Integer, text As String)
        _api.MarginJump($"{zoneId}_A_T", panel, top, Nothing)
        _api.MarginJump($"{zoneId}_A_B", panel, bottom, Nothing)
        _api.MarginJump($"{zoneId}_A_L", panel, Nothing, left)
        _api.MarginJump($"{zoneId}_A_R", panel, Nothing, right)

        Dim zone = _menuSystem.GetPooledZoneRef(zoneId)
        If zone IsNot Nothing Then zone.Text = text

        _api.SwitchZoneToMarginSetB(zoneId)
        RegisterZone(zone)
    End Sub

    Private Sub HideAndReleaseZone(zoneId As String)
        Dim zone = _menuSystem.GetPooledZoneRef(zoneId)
        UnregisterZone(zone)
        _api.SwitchZoneToMarginSetA(zoneId)
        _hoveredZones.TryRemove(zoneId, Nothing)
        _menuSystem.ReleaseZone(zoneId)
    End Sub

    Public Function HitTest(panel As Object, row As Integer, col As Integer) As Boolean
        If Not _isVisible Then Return False
        If panel IsNot Nothing AndAlso panel.ToString() <> _currentPanel.ToString() Then Return False
        Return IsInsideAnyBounds(row, col)
    End Function

    ' ====================
    ' == HIDE
    ' ====================

    Public Sub Hide()
        If Not _isVisible Then Return
        HideAll()
    End Sub

    Private Sub HideAll()
        _pinnedItem = Nothing
        _pinnedDepth = -1

        For Each state In _subMenuStack
            Dim menuId = GetSubmenuId(state.Depth)
            _menuSystem?.UnregisterActiveMenu(menuId)
            For Each item In state.Items
                HideAndReleaseZone(item.ZoneId)
            Next
        Next
        _subMenuStack.Clear()

        _menuSystem?.UnregisterActiveMenu(GetMainMenuId())

        For Each item In _mainItems
            HideAndReleaseZone(item.ZoneId)
        Next
        _mainItems.Clear()
        _mainZoneIds.Clear()

        _isVisible = False
        _inSelectionFeedback = False
        _collapsedLevels.Clear()
    End Sub

    Private Sub HideWithFeedback(selectedItem As ContextMenuItem)
        _inSelectionFeedback = True
        _pinnedItem = Nothing
        _pinnedDepth = -1

        For Each state In _subMenuStack
            _menuSystem?.UnregisterActiveMenu(GetSubmenuId(state.Depth))
        Next
        _menuSystem?.UnregisterActiveMenu(GetMainMenuId())

        Dim allZones As New List(Of String)()
        For Each item In _mainItems
            allZones.Add(item.ZoneId)
        Next
        For Each state In _subMenuStack
            For Each item In state.Items
                allZones.Add(item.ZoneId)
            Next
        Next

        For Each zoneId In allZones
            If zoneId <> selectedItem.ZoneId Then
                HideAndReleaseZone(zoneId)
            Else
                UnregisterZone(_menuSystem.GetPooledZoneRef(zoneId))
            End If
        Next

        Dim selZoneId = selectedItem.ZoneId
        Dim t As New Thread(Sub()
                                Thread.Sleep(200)
                                _api.SwitchZoneToMarginSetA(selZoneId)
                                _menuSystem.ReleaseZone(selZoneId)
                                _inSelectionFeedback = False
                            End Sub) With {.IsBackground = True}
        t.Start()

        _mainItems.Clear()
        _mainZoneIds.Clear()
        _subMenuStack.Clear()
        _collapsedLevels.Clear()
        _isVisible = False
    End Sub

    ' ====================
    ' == EVENT HANDLERS
    ' ====================

    Private Sub OnZoneEnter(zoneId As String)
        _hoveredZones(zoneId) = True

        If Not IsLevelCollapsed(GetMainMenuId()) Then
            Dim mainItem = _mainItems.FirstOrDefault(Function(i) i.ZoneId = zoneId)
            If mainItem IsNot Nothing Then
                HandleMainItemEnter(mainItem)
                Return
            End If
        End If

        For Each state In _subMenuStack
            If IsLevelCollapsed(GetSubmenuId(state.Depth)) Then Continue For
            Dim subItem = state.Items.FirstOrDefault(Function(i) i.ZoneId = zoneId)
            If subItem IsNot Nothing Then
                HandleSubItemEnter(subItem, state)
                Return
            End If
        Next
    End Sub

    Private Sub HandleMainItemEnter(item As ContextMenuItem)
        If _pinnedItem IsNot Nothing AndAlso _pinnedItem IsNot item Then Return

        If item.HasSubmenu Then
            HideSubmenusAtDepth(0)
            UpdateItemTextToActive(item)
            Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
            If zone IsNot Nothing Then
                ShowSubmenu(item.Name, _currentPanel, zone.Top, zone.Bottom, zone.Left, zone.Right, 0, "__ContextMenu__")
            End If
        Else
            If _pinnedItem Is Nothing Then
                RevertAllSubmenuHeaderTexts()
                HideSubmenusAtDepth(0)
            End If
        End If
    End Sub

    Private Sub HandleSubItemEnter(item As ContextMenuItem, parentState As SubMenuState)
        Dim nextDepth = parentState.Depth + 1

        If _pinnedItem IsNot Nothing AndAlso _pinnedDepth <= item.Depth AndAlso _pinnedItem IsNot item Then Return

        If item.HasSubmenu Then
            HideSubmenusAtDepth(nextDepth)
            UpdateItemTextToActive(item)
            Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
            If zone IsNot Nothing Then
                ShowSubmenu(item.Name, _currentPanel, zone.Top, zone.Bottom, zone.Left, zone.Right, nextDepth, parentState.MenuSource)
            End If
        Else
            If _pinnedItem Is Nothing OrElse _pinnedDepth > nextDepth Then
                HideSubmenusAtDepth(nextDepth)
            End If
        End If
    End Sub

    Private Sub OnZoneLeave(zoneId As String)
        _hoveredZones.TryRemove(zoneId, Nothing)
        ScheduleCollapseCheck()
    End Sub

    Private Sub ScheduleCollapseCheck()
        If _collapseCheckPending Then Return
        _collapseCheckPending = True

        Dim t As New Thread(Sub()
                                Thread.Sleep(100)
                                _collapseCheckPending = False

                                If _inSelectionFeedback OrElse Not _isVisible Then Return
                                If _pinnedItem IsNot Nothing Then Return

                                Dim anyHovered = _hoveredZones.Any(Function(kv) kv.Value)
                                If Not anyHovered Then
                                    RevertAllSubmenuHeaderTexts()
                                    HideSubmenusAtDepth(0)
                                End If
                            End Sub) With {.IsBackground = True}
        t.Start()
    End Sub

    Private Sub OnZoneClickLeft(zoneId As String)
        If Not _isVisible Then Return

        If Not IsLevelCollapsed(GetMainMenuId()) Then
            Dim mainItem = _mainItems.FirstOrDefault(Function(i) i.ZoneId = zoneId)
            If mainItem IsNot Nothing Then
                If mainItem.HasSubmenu Then
                    If _pinnedItem Is mainItem Then UnpinSubmenu() Else PinSubmenu(mainItem, 0)
                    Return
                End If
                _onItemSelected?.Invoke("__ContextMenu__", mainItem.Name, mainItem.Index, "")
                HideWithFeedback(mainItem)
                Return
            End If
        End If

        For Each state In _subMenuStack
            If IsLevelCollapsed(GetSubmenuId(state.Depth)) Then Continue For
            Dim subItem = state.Items.FirstOrDefault(Function(i) i.ZoneId = zoneId)
            If subItem IsNot Nothing Then
                If subItem.HasSubmenu Then
                    If _pinnedItem Is subItem Then UnpinSubmenu() Else PinSubmenu(subItem, state.Depth + 1)
                    Return
                End If
                _onItemSelected?.Invoke(state.MenuSource, subItem.Name, subItem.Index, state.ParentItemName)
                Dim cb = _dropdownCollapseCallback
                _dropdownCollapseCallback = Nothing
                HideWithFeedback(subItem)
                cb?.Invoke()
                Return
            End If
        Next
    End Sub

    Private Sub PinSubmenu(item As ContextMenuItem, depth As Integer)
        _pinnedItem = item
        _pinnedDepth = depth
        UpdateItemTextToActive(item)
        Console.WriteLine($"[ContextMenu] Pinned '{item.Name}' at depth {depth}")
    End Sub

    Private Sub UnpinSubmenu()
        If _pinnedItem Is Nothing Then Return
        Dim itemToUnpin = _pinnedItem
        Dim depthToClose = _pinnedDepth
        Console.WriteLine($"[ContextMenu] Unpinning '{itemToUnpin.Name}'")
        _pinnedItem = Nothing
        _pinnedDepth = -1
        RevertItemTextToInactive(itemToUnpin)
        HideSubmenusAtDepth(depthToClose)
    End Sub

    Private Sub OnGlobalLeftClick(evt As Object)
        _justShownThisEvent = False
        If Not _isVisible OrElse _inSelectionFeedback Then Return
        ' By RAY. HideAll below parks and releases every zone here; harmless, the
        ' answer having been taken before this handler ran.
        If _menuSystem IsNot Nothing AndAlso
           ContextOwnsZone(_menuSystem.ResolveClickOwner(evt)) Then Return
        HideAll()
    End Sub

    Private Sub OnGlobalRightClick()
        If _justShownThisEvent Then
            _justShownThisEvent = False
            Return
        End If
        If Not _isVisible OrElse _inSelectionFeedback Then Return
        HideAll()
    End Sub

    Private Function IsInsideAnyBounds(row As Integer, col As Integer) As Boolean
        If _mainItems.Count > 0 AndAlso Not IsLevelCollapsed(GetMainMenuId()) Then
            If col >= _mainLeft AndAlso col <= _mainRight AndAlso row >= _mainTop AndAlso row <= _mainBottom Then Return True
        End If

        For Each state In _subMenuStack
            If IsLevelCollapsed(GetSubmenuId(state.Depth)) Then Continue For
            If col >= state.Left AndAlso col <= state.Right AndAlso row >= state.Top AndAlso row <= state.Bottom Then Return True
        Next

        Return False
    End Function

    ' ====================
    ' == HELPERS
    ' ====================

    Private Sub RegisterZone(zone As ISpatialZone)
        If zone Is Nothing OrElse _aggregator Is Nothing Then Return
        Try : CallByName(_aggregator, "RegisterZoneForMouseEvents", CallType.Method, zone) : Catch : End Try
    End Sub

    Private Sub UnregisterZone(zone As ISpatialZone)
        If zone Is Nothing OrElse _aggregator Is Nothing Then Return
        Try : CallByName(_aggregator, "UnregisterZoneForMouseEvents", CallType.Method, zone) : Catch : End Try
    End Sub

    Private Function SafeGetString(obj As Object, propName As String) As String
        If obj Is Nothing Then Return ""
        Try : Return CStr(CallByName(obj, propName, CallType.Get)) : Catch : Return "" : End Try
    End Function

End Class

Public Class ContextMenuItem
    Public ReadOnly Property ZoneId As String
    Public ReadOnly Property Name As String
    Public ReadOnly Property Index As Integer
    Public ReadOnly Property HasSubmenu As Boolean
    Public ReadOnly Property Depth As Integer
    Public Sub New(zoneId As String, name As String, index As Integer, hasSubmenu As Boolean, depth As Integer)
        Me.ZoneId = zoneId
        Me.Name = name
        Me.Index = index
        Me.HasSubmenu = hasSubmenu
        Me.Depth = depth
    End Sub
End Class

Public Class SubMenuState
    Public Property Depth As Integer
    Public Property ParentItemName As String
    Public Property MenuSource As String
    Public Property Top As Integer
    Public Property Bottom As Integer
    Public Property Left As Integer
    Public Property Right As Integer
    Public Property CharWidth As Integer
    Public Property Items As New List(Of ContextMenuItem)()
    Public Property ZoneIds As New HashSet(Of String)()
End Class