'MenuInstance.vb
Imports System.Collections.Concurrent
Imports System.Threading
Imports Current.PluginApi

Public Class MenuInstance
    Implements IDisposable

    Private Const CharWidth As Integer = 5
    Private Const CharHeight As Integer = 7
    Private Const Gutter As Integer = 1

    Private Shared ReadOnly _zoneHeightBase As Integer = CharHeight + 2 * Gutter + 1

    Public Shared Function GetZoneHeight() As Integer
        Return _zoneHeightBase
    End Function

    Public Shared Function CalculateZoneWidth(charCount As Integer) As Integer
        Return charCount * CharWidth + (charCount + 1) * Gutter + 1
    End Function

    Private ReadOnly _menuName As String
    Private ReadOnly _panel As PanelType
    Private ReadOnly _anchorRow As Integer
    Private ReadOnly _anchorCol As Integer
    Private ReadOnly _api As ICurrentApi
    Private ReadOnly _aggregator As Object
    Private ReadOnly _contextMenuMgr As ContextMenuManager
    Private ReadOnly _menuSystem As MenuSystemPlugin
    Private ReadOnly _hostCharWidth As Integer
    Private ReadOnly _itemCharWidth As Integer

    Private _hostZoneId As String

    Private ReadOnly _items As New List(Of DropdownItem)()
    Private ReadOnly _itemsByName As New Dictionary(Of String, DropdownItem)()
    Private ReadOnly _hoveredZones As New ConcurrentDictionary(Of String, Boolean)()
    Private ReadOnly _sessionLock As New Object()

    Private _isExpanded As Boolean = False
    Private _disposed As Boolean = False
    Private _pinnedSubmenuItem As DropdownItem = Nothing
    Private _isMenuPinned As Boolean = False

    Private _hostCollapsed As Boolean = False
    Private _itemsCollapsed As Boolean = False

    Private _enterCallback As Action(Of Object)
    Private _leaveCallback As Action(Of Object)
    Private _clickLeftCallback As Action(Of Object)
    Private _clickRightCallback As Action(Of Object)
    Private _globalLeftClickCallback As Action(Of Object)
    Private _globalRightClickCallback As Action(Of Object)

    Public Sub New(menuName As String, panel As PanelType, anchorRow As Integer,
                   anchorCol As Integer, api As ICurrentApi, aggregator As Object,
                   contextMenuMgr As ContextMenuManager, menuSystem As MenuSystemPlugin,
                   hostCharWidth As Integer, itemCharWidth As Integer)
        _menuName = menuName
        _panel = panel
        _anchorRow = anchorRow
        _anchorCol = anchorCol
        _api = api
        _aggregator = aggregator
        _contextMenuMgr = contextMenuMgr
        _menuSystem = menuSystem
        _hostCharWidth = hostCharWidth
        _itemCharWidth = itemCharWidth
    End Sub

    Public Sub Initialize()
        CreateHostZone()
        RegisterHostForMouseEvents()
        SubscribeToEvents()
        RegisterWithOverlapSystem()
    End Sub

    ' ====================
    ' == OVERLAP MANAGEMENT
    ' ====================

    Private Function GetHostMenuId() As String
        Return $"Dropdown_{_menuName}_Host"
    End Function

    Private Function GetItemsMenuId() As String
        Return $"Dropdown_{_menuName}_Items"
    End Function

    Private Sub RegisterWithOverlapSystem()
        If _menuSystem Is Nothing Then Return
        Dim hostBounds = CalculateHostBounds()
        _menuSystem.RegisterActiveMenu(GetHostMenuId(), hostBounds, AddressOf OnHostOverlapCollapse, AddressOf OnHostOverlapRestore)
    End Sub

    Private Sub UnregisterFromOverlapSystem()
        _menuSystem?.UnregisterActiveMenu(GetHostMenuId())
        _menuSystem?.UnregisterActiveMenu(GetItemsMenuId())
    End Sub

    Private Sub RegisterExpandedItems()
        If _menuSystem Is Nothing OrElse Not _isExpanded Then Return
        Dim itemsBounds = CalculateItemsBounds()
        ' Register the expanded items' bounding box with the overlap system so the
        ' overlap manager can collapse or restore menus when visible conflicts occur.
        _menuSystem.RegisterActiveMenu(GetItemsMenuId(), itemsBounds, AddressOf OnItemsOverlapCollapse, AddressOf OnItemsOverlapRestore)
        _menuSystem.NotifyMenuShown(GetItemsMenuId())
    End Sub

    Private Sub UnregisterExpandedItems()
        _menuSystem?.UnregisterActiveMenu(GetItemsMenuId())
    End Sub

    Private Function CalculateHostBounds() As ((Integer, Integer, Integer), (Integer, Integer, Integer))
        Dim hostZone = _menuSystem.GetPooledZoneRef(_hostZoneId)
        If hostZone Is Nothing Then Return ((0, 0, 0), (0, 0, 0))
        Return hostZone.BoundingBoxAABB
    End Function

    Private Function CalculateItemsBounds() As ((Integer, Integer, Integer), (Integer, Integer, Integer))
        Dim minX = Integer.MaxValue, minY = Integer.MaxValue, minZ = Integer.MaxValue
        Dim maxX = Integer.MinValue, maxY = Integer.MinValue, maxZ = Integer.MinValue

        For Each item In _items
            Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
            If zone IsNot Nothing Then
                Dim bb = zone.BoundingBoxAABB
                UpdateMinMax(bb, minX, minY, minZ, maxX, maxY, maxZ)
            End If
        Next

        If minX = Integer.MaxValue Then Return ((0, 0, 0), (0, 0, 0))
        Return ((minX, minY, minZ), (maxX, maxY, maxZ))
    End Function

    Private Function CalculateOverallBounds() As ((Integer, Integer, Integer), (Integer, Integer, Integer))
        Dim minX = Integer.MaxValue, minY = Integer.MaxValue, minZ = Integer.MaxValue
        Dim maxX = Integer.MinValue, maxY = Integer.MinValue, maxZ = Integer.MinValue

        Dim hostZone = _menuSystem.GetPooledZoneRef(_hostZoneId)
        If hostZone IsNot Nothing Then
            Dim bb = hostZone.BoundingBoxAABB
            UpdateMinMax(bb, minX, minY, minZ, maxX, maxY, maxZ)
        End If

        If _isExpanded Then
            For Each item In _items
                Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
                If zone IsNot Nothing Then
                    Dim bb = zone.BoundingBoxAABB
                    UpdateMinMax(bb, minX, minY, minZ, maxX, maxY, maxZ)
                End If
            Next
        End If

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

    Private Sub OnHostOverlapCollapse()
        If _hostCollapsed Then Return
        _hostCollapsed = True

        Dim hostZone = _menuSystem.GetPooledZoneRef(_hostZoneId)
        If hostZone IsNot Nothing Then
            Try : CallByName(_aggregator, "UnregisterZoneForMouseEvents", CallType.Method, hostZone) : Catch : End Try
        End If
        _api.SwitchZoneToMarginSetA(_hostZoneId)

        If _isExpanded AndAlso Not _itemsCollapsed Then
            OnItemsOverlapCollapse()
        End If

        _contextMenuMgr?.HideAllSubmenus()
        Console.WriteLine($"[MenuInstance] '{_menuName}' host collapsed due to overlap")
    End Sub

    Private Sub OnHostOverlapRestore()
        If Not _hostCollapsed Then Return
        _hostCollapsed = False

        _api.SwitchZoneToMarginSetB(_hostZoneId)
        Dim hostZone = _menuSystem.GetPooledZoneRef(_hostZoneId)
        If hostZone IsNot Nothing Then
            Try : CallByName(_aggregator, "RegisterZoneForMouseEvents", CallType.Method, hostZone) : Catch : End Try
        End If

        If _isExpanded AndAlso _itemsCollapsed Then
            OnItemsOverlapRestore()
        End If

        Console.WriteLine($"[MenuInstance] '{_menuName}' host restored")
    End Sub

    Private Sub OnItemsOverlapCollapse()
        If _itemsCollapsed OrElse Not _isExpanded Then Return
        _itemsCollapsed = True

        For Each item In _items
            Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
            If zone IsNot Nothing Then
                Try : CallByName(_aggregator, "UnregisterZoneForMouseEvents", CallType.Method, zone) : Catch : End Try
            End If
            _api.SwitchZoneToMarginSetA(item.ZoneId)
        Next

        _contextMenuMgr?.HideAllSubmenus()
        Console.WriteLine($"[MenuInstance] '{_menuName}' items collapsed due to overlap")
    End Sub

    Private Sub OnItemsOverlapRestore()
        If Not _itemsCollapsed OrElse Not _isExpanded Then Return
        _itemsCollapsed = False

        For Each item In _items
            _api.SwitchZoneToMarginSetB(item.ZoneId)
            Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
            If zone IsNot Nothing Then
                Try : CallByName(_aggregator, "RegisterZoneForMouseEvents", CallType.Method, zone) : Catch : End Try
            End If
        Next

        Console.WriteLine($"[MenuInstance] '{_menuName}' items restored")
    End Sub

    Private Function IsCollapsedByOverlap() As Boolean
        Return _hostCollapsed OrElse _itemsCollapsed
    End Function

    ' ====================
    ' == SUBMENU DEFINITION
    ' ====================

    Public Sub DefineSubmenu(parentItemName As String, childItems() As String)
        If _contextMenuMgr Is Nothing Then Return
        _contextMenuMgr.DefineSubmenu(parentItemName, childItems)

        If _itemsByName.ContainsKey(parentItemName) Then
            Dim item = _itemsByName(parentItemName)
            Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
            If zone IsNot Nothing Then zone.Text = "* " & parentItemName
            item.HasSubmenu = True
        End If
    End Sub

    ' ====================
    ' == HOST ZONE (POOLED)
    ' ====================

    Private Sub CreateHostZone()
        _hostZoneId = _menuSystem.AcquireZone()

        Dim insideW = _hostCharWidth * CharWidth + (_hostCharWidth + 1) * Gutter
        Dim insideH = CharHeight + 2 * Gutter

        Dim leftCol = _anchorCol
        Dim rightCol = leftCol + insideW + 1
        Dim topRow = _anchorRow
        Dim bottomRow = topRow + insideH + 1

        _api.MarginJump($"{_hostZoneId}_A_T", _panel, topRow, Nothing)
        _api.MarginJump($"{_hostZoneId}_A_B", _panel, bottomRow, Nothing)
        _api.MarginJump($"{_hostZoneId}_A_L", _panel, Nothing, leftCol)
        _api.MarginJump($"{_hostZoneId}_A_R", _panel, Nothing, rightCol)

        Dim zone = _menuSystem.GetPooledZoneRef(_hostZoneId)
        If zone IsNot Nothing Then zone.Text = _menuName

        _api.SwitchZoneToMarginSetB(_hostZoneId)
    End Sub

    Private Sub RegisterHostForMouseEvents()
        If _aggregator Is Nothing Then Return
        Dim hostZone = _menuSystem.GetPooledZoneRef(_hostZoneId)
        If hostZone Is Nothing Then Return
        Try
            CallByName(_aggregator, "RegisterZoneForMouseEvents", CallType.Method, hostZone)
        Catch ex As Exception
            Console.WriteLine($"[MenuInstance] Failed to register host: {ex.Message}")
        End Try
    End Sub

    ' ====================
    ' == EVENT SUBSCRIPTIONS
    ' ====================

    Private Sub SubscribeToEvents()
        If _aggregator Is Nothing Then Return

        _enterCallback = Sub(evt) OnZoneEnter(SafeGetString(evt, "ZoneId"))
        _leaveCallback = Sub(evt) OnZoneLeave(SafeGetString(evt, "ZoneId"))
        _clickLeftCallback = Sub(evt) OnZoneClickLeft(SafeGetString(evt, "ZoneId"))
        _clickRightCallback = Sub(evt) OnZoneClickRight(SafeGetString(evt, "ZoneId"))
        _globalLeftClickCallback = Sub(evt) OnGlobalLeftClick(evt)
        _globalRightClickCallback = Sub(evt) OnGlobalRightClick(evt)

        Try
            CallByName(_aggregator, "Subscribe", CallType.Method, "SpatialZoneMouseEnter", _enterCallback)
            CallByName(_aggregator, "Subscribe", CallType.Method, "SpatialZoneMouseLeave", _leaveCallback)
            CallByName(_aggregator, "Subscribe", CallType.Method, "SpatialZoneMouseClickLeft", _clickLeftCallback)
            CallByName(_aggregator, "Subscribe", CallType.Method, "SpatialZoneMouseClickRight", _clickRightCallback)
            CallByName(_aggregator, "Subscribe", CallType.Method, "MouseClickLeft", _globalLeftClickCallback)
            CallByName(_aggregator, "Subscribe", CallType.Method, "MouseClickRight", _globalRightClickCallback)
        Catch ex As Exception
            Console.WriteLine($"[MenuInstance] Subscribe failed: {ex.Message}")
        End Try
    End Sub

    Private Sub UnsubscribeFromEvents()
        If _aggregator Is Nothing Then Return
        Try
            If _enterCallback IsNot Nothing Then CallByName(_aggregator, "Unsubscribe", CallType.Method, "SpatialZoneMouseEnter", _enterCallback)
            If _leaveCallback IsNot Nothing Then CallByName(_aggregator, "Unsubscribe", CallType.Method, "SpatialZoneMouseLeave", _leaveCallback)
            If _clickLeftCallback IsNot Nothing Then CallByName(_aggregator, "Unsubscribe", CallType.Method, "SpatialZoneMouseClickLeft", _clickLeftCallback)
            If _clickRightCallback IsNot Nothing Then CallByName(_aggregator, "Unsubscribe", CallType.Method, "SpatialZoneMouseClickRight", _clickRightCallback)
            If _globalLeftClickCallback IsNot Nothing Then CallByName(_aggregator, "Unsubscribe", CallType.Method, "MouseClickLeft", _globalLeftClickCallback)
            If _globalRightClickCallback IsNot Nothing Then CallByName(_aggregator, "Unsubscribe", CallType.Method, "MouseClickRight", _globalRightClickCallback)
        Catch : End Try
    End Sub

    ' ====================
    ' == EVENT HANDLERS
    ' ====================

    Private Sub OnZoneEnter(zoneId As String)
        If Not IsMyZone(zoneId) OrElse IsCollapsedByOverlap() Then Return
        _hoveredZones(zoneId) = True

        If zoneId = _hostZoneId Then
            If Not _isExpanded Then Expand()
        Else
            Dim item = _items.FirstOrDefault(Function(i) i.ZoneId = zoneId)
            If item IsNot Nothing Then
                If item.HasSubmenu AndAlso _contextMenuMgr IsNot Nothing Then
                    If _pinnedSubmenuItem IsNot Nothing AndAlso _pinnedSubmenuItem.ZoneId <> zoneId Then Return
                    ShowSubmenuForItem(item)
                Else
                    If _pinnedSubmenuItem Is Nothing Then
                        CloseAllSubmenus()
                    End If
                End If
            End If
        End If
    End Sub

    Private Sub OnZoneLeave(zoneId As String)
        If Not IsMyZone(zoneId) Then Return
        Dim dummy As Boolean
        _hoveredZones.TryRemove(zoneId, dummy)

        If _isExpanded Then
            Dim item = _items.FirstOrDefault(Function(i) i.ZoneId = zoneId AndAlso i.HasSubmenu)
            If item IsNot Nothing AndAlso item IsNot _pinnedSubmenuItem Then
                RevertSubmenuHeaderText(item)
            End If
        End If

        ScheduleCollapseCheck()
    End Sub

    Private Sub OnZoneClickLeft(zoneId As String)
        If Not IsMyZone(zoneId) OrElse IsCollapsedByOverlap() Then Return

        If zoneId = _hostZoneId Then
            HandleHostClick()
        Else
            Dim item = _items.FirstOrDefault(Function(i) i.ZoneId = zoneId)
            If item IsNot Nothing Then
                If item.HasSubmenu Then
                    If _pinnedSubmenuItem Is item Then UnpinSubmenu() Else PinSubmenuForItem(item)
                    Return
                End If
                PublishItemSelected(item)
                CollapseWithSelectedFeedback(item)
            End If
        End If
    End Sub

    Private Sub HandleHostClick()
        If _isMenuPinned Then
            _isMenuPinned = False
            Console.WriteLine($"[MenuInstance] Menu '{_menuName}' unpinned (hover state)")
        ElseIf _isExpanded Then
            _isMenuPinned = True
            Console.WriteLine($"[MenuInstance] Menu '{_menuName}' pinned")
        Else
            Expand()
            _isMenuPinned = True
            Console.WriteLine($"[MenuInstance] Menu '{_menuName}' expanded and pinned")
        End If
    End Sub

    Private Sub OnZoneClickRight(zoneId As String)
        If Not IsMyZone(zoneId) OrElse IsCollapsedByOverlap() Then Return
        If _isExpanded Then Collapse()
    End Sub

    Private Sub OnGlobalLeftClick(evt As Object)
        If Not _isExpanded Then Return
        If OwnsClick(evt) Then Return
        _isMenuPinned = False
        UnpinSubmenu()
        Collapse()
    End Sub

    Private Sub OnGlobalRightClick(evt As Object)
        If Not _isExpanded Then Return
        If OwnsClick(evt) Then Return
        _isMenuPinned = False
        UnpinSubmenu()
        Collapse()
    End Sub

    ' ====================
    ' == SUBMENU DISPLAY HELPERS
    ' ====================

    Private Sub ShowSubmenuForItem(item As DropdownItem)
        Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
        If zone Is Nothing Then Return
        zone.Text = "X " & item.Name
        _contextMenuMgr.ShowSubmenu(item.Name, _panel, zone.Top, zone.Bottom,
                                     zone.Left, zone.Right, 0, _menuName,
                                     AddressOf CollapseForSubmenuSelection)
    End Sub

    Private Sub PinSubmenuForItem(item As DropdownItem)
        If _pinnedSubmenuItem IsNot Nothing AndAlso _pinnedSubmenuItem IsNot item Then
            UnpinSubmenu()
        End If

        _pinnedSubmenuItem = item
        ShowSubmenuForItem(item)

        _contextMenuMgr?.SetPinnedSubmenu(item.Name, 0)

        Console.WriteLine($"[MenuInstance] Pinned submenu for '{item.Name}'")
    End Sub

    Private Sub UnpinSubmenu()
        If _pinnedSubmenuItem Is Nothing Then Return
        Dim itemName = _pinnedSubmenuItem.Name

        _contextMenuMgr?.ClearPinnedSubmenu()

        RevertSubmenuHeaderText(_pinnedSubmenuItem)
        _contextMenuMgr?.HideAllSubmenus()
        Console.WriteLine($"[MenuInstance] Unpinned submenu for '{itemName}'")
        _pinnedSubmenuItem = Nothing
    End Sub

    Private Sub CloseAllSubmenus()
        For Each item In _items
            If item.HasSubmenu Then
                RevertSubmenuHeaderText(item)
            End If
        Next
        _contextMenuMgr?.HideAllSubmenus()
    End Sub

    Private Sub RevertSubmenuHeaderText(item As DropdownItem)
        If item Is Nothing OrElse Not item.HasSubmenu Then Return
        Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
        If zone IsNot Nothing Then zone.Text = "* " & item.Name
    End Sub

    Private Sub PublishItemSelected(item As DropdownItem)
        If _aggregator Is Nothing Then Return
        Dim payload = New With {
            .MenuName = _menuName,
            .ItemName = item.Name,
            .ItemIndex = item.Index,
            .ParentItem = ""
        }
        Try
            CallByName(_aggregator, "Publish", CallType.Method, "MenuItemSelected", payload)
            Console.WriteLine($"[MenuInstance] Selected '{item.Name}' in '{_menuName}'.")
        Catch : End Try
    End Sub

    ' ====================
    ' == ITEM MANAGEMENT
    ' ====================

    Public Sub AddItem(itemName As String)
        If _itemsByName.ContainsKey(itemName) Then
            Console.WriteLine($"[MenuInstance] Item '{itemName}' already exists.")
            Return
        End If
        If _isExpanded Then Collapse()

        Dim itemIndex = _items.Count
        Dim hasSub = _contextMenuMgr IsNot Nothing AndAlso _contextMenuMgr.HasSubmenu(itemName)
        Dim item As New DropdownItem(_menuName, itemIndex, itemName, hasSub)
        _items.Add(item)
        _itemsByName(itemName) = item
        CreateItemZone(item)
    End Sub

    Public Function RemoveItem(itemName As String) As Boolean
        If Not _itemsByName.ContainsKey(itemName) Then Return False
        If _isExpanded Then Collapse()

        Dim itemToRemove = _itemsByName(itemName)

        UnregisterItemZone(itemToRemove)
        _menuSystem.ReleaseZone(itemToRemove.ZoneId)

        _items.Remove(itemToRemove)
        _itemsByName.Remove(itemName)

        Dim remaining = _items.Select(Function(i) i.Name).ToList()
        _items.Clear()
        _itemsByName.Clear()

        For Each name In remaining
            Dim idx = _items.Count
            Dim hasSub = _contextMenuMgr IsNot Nothing AndAlso _contextMenuMgr.HasSubmenu(name)
            Dim newItem As New DropdownItem(_menuName, idx, name, hasSub)
            _items.Add(newItem)
            _itemsByName(name) = newItem
            CreateItemZone(newItem)
        Next

        Return True
    End Function

    Public Sub ClearItems()
        If _isExpanded Then Collapse()

        For Each item In _items.ToList()
            UnregisterItemZone(item)
            _menuSystem.ReleaseZone(item.ZoneId)
        Next

        _items.Clear()
        _itemsByName.Clear()
    End Sub

    Public Function GetItemNamesInOrder() As List(Of String)
        Return _items.Select(Function(i) i.Name).ToList()
    End Function

    Public Function GetItemCount() As Integer
        Return _items.Count
    End Function

    ' ====================
    ' == REORDER
    ' ====================

    Public Sub ReorderItems(newOrder As List(Of String))
        If _isExpanded Then Collapse()

        For Each item In _items
            UnregisterItemZone(item)
            _menuSystem.ReleaseZone(item.ZoneId)
        Next

        _items.Clear()
        _itemsByName.Clear()

        For Each name In newOrder
            Dim idx = _items.Count
            Dim hasSub = _contextMenuMgr IsNot Nothing AndAlso _contextMenuMgr.HasSubmenu(name)
            Dim item As New DropdownItem(_menuName, idx, name, hasSub)
            _items.Add(item)
            _itemsByName(name) = item
            CreateItemZone(item)
        Next
    End Sub

    ' ====================
    ' == ITEM ZONE CREATION
    ' ====================

    Private Sub CreateItemZone(item As DropdownItem)
        Dim hostZone = _menuSystem.GetPooledZoneRef(_hostZoneId)
        If hostZone Is Nothing Then Return

        Dim zoneId = _menuSystem.AcquireZone()
        item.ZoneId = zoneId

        Dim hostBottom = hostZone.Bottom
        Dim insideW = _itemCharWidth * CharWidth + (_itemCharWidth + 1) * Gutter
        Dim insideH = CharHeight + 2 * Gutter

        Dim panelRight = _api.GetPanelFurthestRightColumn(_panel)
        Dim panelLeft = _api.GetPanelFurthestLeftColumn(_panel)

        Dim rightCol = _anchorCol + insideW + 1
        Dim leftCol = _anchorCol

        If rightCol > panelRight Then
            rightCol = panelRight
            leftCol = rightCol - insideW - 1
            If leftCol < panelLeft Then leftCol = panelLeft
        End If

        Dim expandedTopRow = hostBottom + (item.Index * (insideH + 1))
        Dim expandedBottomRow = expandedTopRow + insideH + 1

        _api.MarginJump($"{zoneId}_A_T", _panel, expandedTopRow, Nothing)
        _api.MarginJump($"{zoneId}_A_B", _panel, expandedBottomRow, Nothing)
        _api.MarginJump($"{zoneId}_A_L", _panel, Nothing, leftCol)
        _api.MarginJump($"{zoneId}_A_R", _panel, Nothing, rightCol)

        Dim displayText = If(item.HasSubmenu, "* " & item.Name, item.Name)
        Dim zone = _menuSystem.GetPooledZoneRef(zoneId)
        If zone IsNot Nothing Then zone.Text = displayText

        _api.SwitchZoneToMarginSetA(zoneId)
    End Sub

    Private Sub UnregisterItemZone(item As DropdownItem)
        If _aggregator Is Nothing Then Return
        Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
        If zone IsNot Nothing Then
            Try : CallByName(_aggregator, "UnregisterZoneForMouseEvents", CallType.Method, zone) : Catch : End Try
        End If
    End Sub

    ' ====================
    ' == EXPAND / COLLAPSE
    ' ====================

    Private Sub Expand()
        If _isExpanded OrElse _items.Count = 0 OrElse _hostCollapsed Then Return

        For Each item In _items
            _api.SwitchZoneToMarginSetB(item.ZoneId)
            Dim zone = _menuSystem.GetPooledZoneRef(item.ZoneId)
            If zone IsNot Nothing AndAlso _aggregator IsNot Nothing Then
                Try : CallByName(_aggregator, "RegisterZoneForMouseEvents", CallType.Method, zone) : Catch : End Try
            End If
        Next

        _isExpanded = True
        _itemsCollapsed = False
        RegisterExpandedItems()
    End Sub

    Private Sub Collapse()
        If Not _isExpanded Then Return

        _isMenuPinned = False
        UnpinSubmenu()

        _contextMenuMgr?.HideAllSubmenus()

        UnregisterExpandedItems()

        For Each item In _items
            If item.HasSubmenu Then RevertSubmenuHeaderText(item)
            UnregisterItemZone(item)
            _api.SwitchZoneToMarginSetA(item.ZoneId)
        Next

        _isExpanded = False
        _itemsCollapsed = False
    End Sub

    Private Sub CollapseForSubmenuSelection()
        If Not _isExpanded Then Return

        _isMenuPinned = False
        UnpinSubmenu()

        UnregisterExpandedItems()

        For Each item In _items
            If item.HasSubmenu Then RevertSubmenuHeaderText(item)
            UnregisterItemZone(item)
            _api.SwitchZoneToMarginSetA(item.ZoneId)
        Next

        _isExpanded = False
        _itemsCollapsed = False
    End Sub

    Private Sub CollapseWithSelectedFeedback(selectedItem As DropdownItem)
        If Not _isExpanded Then Return

        _isMenuPinned = False
        UnpinSubmenu()

        _contextMenuMgr?.HideAllSubmenusWithFeedback()
        UnregisterExpandedItems()

        For Each item In _items
            If item.HasSubmenu Then RevertSubmenuHeaderText(item)
            UnregisterItemZone(item)
            If item.ZoneId <> selectedItem.ZoneId Then
                _api.SwitchZoneToMarginSetA(item.ZoneId)
            End If
        Next

        _isExpanded = False
        _itemsCollapsed = False

        Dim t As New Thread(Sub()
                                Thread.Sleep(200)
                                Try : _api.SwitchZoneToMarginSetA(selectedItem.ZoneId) : Catch : End Try
                            End Sub) With {.IsBackground = True}
        t.Start()
    End Sub

    Private Sub ScheduleCollapseCheck()
        Dim t As New Thread(Sub()
                                Thread.Sleep(50)
                                SyncLock _sessionLock
                                    If _isMenuPinned Then Return
                                    If _pinnedSubmenuItem IsNot Nothing Then Return

                                    Dim anyHovered = _hoveredZones.Any(Function(kv) kv.Value)
                                    Dim submenuHovered = _contextMenuMgr IsNot Nothing AndAlso _contextMenuMgr.IsSubmenuActiveAndHovered()
                                    If Not anyHovered AndAlso Not submenuHovered AndAlso _isExpanded Then
                                        Collapse()
                                    End If
                                End SyncLock
                            End Sub) With {.IsBackground = True}
        t.Start()
    End Sub

    ' ====================
    ' == HELPERS
    ' ====================


    ' Every zone this dropdown holds.
    Friend Function DropdownOwnsZone(zoneId As String) As Boolean
        If zoneId = "" Then Return False
        If zoneId = _hostZoneId Then Return True
        For Each item In _items
            If item.ZoneId = zoneId Then Return True
        Next
        Return False
    End Function


    ' THE SUBMENU CLAUSE STANDS: a pinned submenu's zones belong to the context manager,
    ' not to us, but a click upon one must not collapse this dropdown.
    ' It survives on ray data.
    Private Function OwnsClick(evt As Object) As Boolean
        If _menuSystem Is Nothing Then Return False
        Dim owner = _menuSystem.ResolveClickOwner(evt)
        If owner = "" Then Return False
        If DropdownOwnsZone(owner) Then Return True
        Return _contextMenuMgr IsNot Nothing AndAlso _contextMenuMgr.ContextOwnsZone(owner)
    End Function



    Private Function IsMyZone(zoneId As String) As Boolean
        If zoneId = _hostZoneId Then Return True
        Return _items.Any(Function(i) i.ZoneId = zoneId)
    End Function

    Private Function SafeGetString(obj As Object, propName As String) As String
        If obj Is Nothing Then Return ""
        Try : Return CStr(CallByName(obj, propName, CallType.Get)) : Catch : Return "" : End Try
    End Function

    ' Panel-aware CELL hit test. Collapsed layers report False.
    ' LEGACY. Its only caller is MenuSystemPlugin.IsClickInsideAnyMenu, which
    ' CadHandPlugin still consults by cell. Ownership is now decided by RAY through
    ' ResolveClickOwner - see OnGlobalLeftClick - and this remains only until that
    ' last cell-shaped caller retires. Do not add callers.
    Public Function HitTest(panel As Object, row As Integer, col As Integer) As Boolean
        If panel IsNot Nothing AndAlso panel.ToString() <> _panel.ToString() Then Return False
        If Not _hostCollapsed Then
            Dim hz = _menuSystem.GetPooledZoneRef(_hostZoneId)
            If hz IsNot Nothing AndAlso col >= hz.Left AndAlso col <= hz.Right AndAlso
           row >= hz.Top AndAlso row <= hz.Bottom Then Return True
        End If
        If _isExpanded AndAlso Not _itemsCollapsed Then
            For Each item In _items
                Dim z = _menuSystem.GetPooledZoneRef(item.ZoneId)
                If z IsNot Nothing AndAlso col >= z.Left AndAlso col <= z.Right AndAlso
               row >= z.Top AndAlso row <= z.Bottom Then Return True
            Next
        End If
        Return False
    End Function

    ' ====================
    ' == DISPOSE
    ' ====================

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True

        If _isExpanded Then Collapse()
        UnsubscribeFromEvents()
        UnregisterFromOverlapSystem()

        Dim hostZone = _menuSystem.GetPooledZoneRef(_hostZoneId)
        If hostZone IsNot Nothing AndAlso _aggregator IsNot Nothing Then
            Try : CallByName(_aggregator, "UnregisterZoneForMouseEvents", CallType.Method, hostZone) : Catch : End Try
        End If

        For Each item In _items.ToList()
            UnregisterItemZone(item)
            _menuSystem.ReleaseZone(item.ZoneId)
        Next
        _items.Clear()
        _itemsByName.Clear()

        _api.SwitchZoneToMarginSetA(_hostZoneId)
        _menuSystem.ReleaseZone(_hostZoneId)

        _hostCollapsed = False
        _itemsCollapsed = False
    End Sub

End Class


Public Class DropdownItem
    Public ReadOnly Property MenuName As String
    Public ReadOnly Property Index As Integer
    Public ReadOnly Property Name As String
    Public Property ZoneId As String
    Public Property HasSubmenu As Boolean

    Public Sub New(menuName As String, index As Integer, name As String, Optional hasSubmenu As Boolean = False)
        Me.MenuName = menuName
        Me.Index = index
        Me.Name = name
        Me.ZoneId = Nothing
        Me.HasSubmenu = hasSubmenu
    End Sub
End Class