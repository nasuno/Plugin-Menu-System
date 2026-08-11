'DropdownMenuProvider.vb
Imports System.Collections.Concurrent
Imports Current.PluginApi

<PluginMetadata("Menu System", "4.0", "Nasuno",
                "Dropdown menu system with pooled context menu, configurable widths, and overlap management.")>
Public Class MenuSystemPlugin
    Implements IPlugin

    Private _api As ICurrentApi
    Private _aggregator As Object
    Private ReadOnly _menus As New ConcurrentDictionary(Of String, MenuInstance)()
    Private _contextMenu As ContextMenuManager

    Private _clickOwner As ClickOwnershipResolver

    ' ====================
    ' == ZONE POOL
    ' ====================

    Private Const InitialPoolSize As Integer = 15
    Private Const PoolGrowthCount As Integer = 2
    Private Const CharWidth As Integer = 5
    Private Const CharHeight As Integer = 7
    Private Const Gutter As Integer = 1

    Private Shared ReadOnly ZoneHeightBase As Integer = CharHeight + 2 * Gutter + 1

    Private ReadOnly _pool As New List(Of String)()
    Private ReadOnly _poolRefs As New Dictionary(Of String, ISpatialZone)()
    Private _poolCounter As Integer = 0
    Private ReadOnly _poolLock As New Object()

    Public Shared Function GetZoneHeight() As Integer
        Return ZoneHeightBase
    End Function

    Public Shared Function CalculateZoneWidth(charCount As Integer) As Integer
        Return charCount * CharWidth + (charCount + 1) * Gutter + 1
    End Function

    ' ====================
    ' == GLOBAL DEFAULTS
    ' ====================

    Public Shared DefaultContextCharWidth As Integer = 14
    Public Shared DefaultContextAutoWidth As Boolean = True
    Public Shared DefaultDropdownHostCharWidth As Integer = 7
    Public Shared DefaultDropdownItemCharWidth As Integer = 10
    Public Shared SubmenuPrefixWidth As Integer = 2

    ' ====================
    ' == OVERLAP MANAGEMENT
    ' ====================

    Private ReadOnly _activeMenus As New ConcurrentDictionary(Of String, ActiveMenuInfo)()
    Private ReadOnly _collapsedBy As New ConcurrentDictionary(Of String, HashSet(Of String))()
    Private _showOrderCounter As Long = 0
    Private ReadOnly _overlapLock As New Object()

    Public Sub Execute(api As ICurrentApi) Implements IPlugin.Execute
        _api = api
        PluginHub.Register("MenuSystem", Me)
        Console.WriteLine("[MenuSystem] Registered.")
        _aggregator = PluginHub.Fetch(Of Object)("EventAggregator")
        If _aggregator Is Nothing Then
            Console.WriteLine("[MenuSystem] Warning: EventAggregator not found.")
        Else
            Console.WriteLine("[MenuSystem] EventAggregator connected.")
        End If

        ' BEFORE EVERYTHING ELSE, AND THIS ORDER IS THE GUARANTEE. Subscribing here
        ' puts the resolver ahead of ContextMenuManager (constructed below), every
        ' MenuInstance (created later still), and many consumer plugins. Subscribing early
        ' ensures we capture the set of spatial zones before other click handlers run.
        _clickOwner = New ClickOwnershipResolver(_api, _aggregator)
        _clickOwner.BeginArbitration()

        CreateInitialPool()
        Console.WriteLine($"[MenuSystem] Zone pool initialized with {InitialPoolSize} zones.")
        _contextMenu = New ContextMenuManager(_api, _aggregator, AddressOf OnContextMenuItemSelected, Me)
        _contextMenu.Initialize()
    End Sub

    ' ====================
    ' == POOL MANAGEMENT
    ' ====================

    Private Sub CreateInitialPool()
        For i As Integer = 0 To InitialPoolSize - 1
            CreatePooledZone()
        Next
    End Sub

    Private Function CreatePooledZone() As String
        SyncLock _poolLock
            _poolCounter += 1
            Dim zoneId = $"__ZonePool_{_poolCounter}__"

            Dim hT = $"{zoneId}_H_T", hB = $"{zoneId}_H_B"
            Dim hL = $"{zoneId}_H_L", hR = $"{zoneId}_H_R"
            _api.CreateMargin(hT, MarginType.RowMargin, PanelType.TopPanel, 0, Nothing, False)
            _api.CreateMargin(hB, MarginType.RowMargin, PanelType.TopPanel, 0, Nothing, False)
            _api.CreateMargin(hL, MarginType.ColumnMargin, PanelType.TopPanel, Nothing, 0, False)
            _api.CreateMargin(hR, MarginType.ColumnMargin, PanelType.TopPanel, Nothing, 0, False)
            _api.CreateMarginSet($"{zoneId}_Hidden", hT, hB, hL, hR)

            Dim aT = $"{zoneId}_A_T", aB = $"{zoneId}_A_B"
            Dim aL = $"{zoneId}_A_L", aR = $"{zoneId}_A_R"
            _api.CreateMargin(aT, MarginType.RowMargin, PanelType.TopPanel, 0, Nothing, False)
            _api.CreateMargin(aB, MarginType.RowMargin, PanelType.TopPanel, 0, Nothing, False)
            _api.CreateMargin(aL, MarginType.ColumnMargin, PanelType.TopPanel, Nothing, 0, False)
            _api.CreateMargin(aR, MarginType.ColumnMargin, PanelType.TopPanel, Nothing, 0, False)
            _api.CreateMarginSet($"{zoneId}_Active", aT, aB, aL, aR)

            Dim zone = _api.CreateSpatialZone(zoneId)
            _api.AssignZoneMarginSetA(zoneId, $"{zoneId}_Hidden")
            _api.AssignZoneMarginSetB(zoneId, $"{zoneId}_Active")
            _api.SwitchZoneToMarginSetA(zoneId)

            _pool.Add(zoneId)
            _poolRefs(zoneId) = zone

            Return zoneId
        End SyncLock
    End Function

    Public Function AcquireZone() As String
        SyncLock _poolLock
            If _pool.Count = 0 Then
                For i As Integer = 0 To PoolGrowthCount - 1
                    CreatePooledZone()
                Next
            End If
            Dim zoneId = _pool(0)
            _pool.RemoveAt(0)
            Return zoneId
        End SyncLock
    End Function

    Public Sub ReleaseZone(zoneId As String)
        SyncLock _poolLock
            If Not _pool.Contains(zoneId) Then _pool.Add(zoneId)
        End SyncLock
    End Sub

    Public Function GetPooledZoneRef(zoneId As String) As ISpatialZone
        SyncLock _poolLock
            Dim zone As ISpatialZone = Nothing
            _poolRefs.TryGetValue(zoneId, zone)
            Return zone
        End SyncLock
    End Function

    ' ====================
    ' == DEFAULT SETTERS
    ' ====================

    Public Sub SetContextMenuDefaults(charWidth As Integer, autoWidth As Boolean)
        If charWidth > 0 Then DefaultContextCharWidth = charWidth
        DefaultContextAutoWidth = autoWidth
        Console.WriteLine($"[MenuSystem] Context defaults: charWidth={DefaultContextCharWidth}, autoWidth={DefaultContextAutoWidth}")
    End Sub

    Public Sub SetDropdownMenuDefaults(hostCharWidth As Integer, itemCharWidth As Integer)
        If hostCharWidth > 0 Then DefaultDropdownHostCharWidth = hostCharWidth
        If itemCharWidth > 0 Then DefaultDropdownItemCharWidth = itemCharWidth
        Console.WriteLine($"[MenuSystem] Dropdown defaults: host={DefaultDropdownHostCharWidth}, item={DefaultDropdownItemCharWidth}")
    End Sub

    ' ====================
    ' == OVERLAP MANAGEMENT API
    ' ====================

    Public Sub RegisterActiveMenu(menuId As String, boundingBox As ((Integer, Integer, Integer), (Integer, Integer, Integer)),
                                   collapseAction As Action, restoreAction As Action)
        SyncLock _overlapLock
            _showOrderCounter += 1
            Dim info As New ActiveMenuInfo() With {
                .MenuId = menuId,
                .BoundingBox = boundingBox,
                .IsCollapsed = False,
                .CollapseAction = collapseAction,
                .RestoreAction = restoreAction,
                .ShowOrder = _showOrderCounter
            }
            _activeMenus(menuId) = info
        End SyncLock
    End Sub

    Public Sub UnregisterActiveMenu(menuId As String)
        SyncLock _overlapLock
            Dim info As ActiveMenuInfo = Nothing
            _activeMenus.TryRemove(menuId, info)
            RestoreMenusCollapsedBy(menuId)
            Dim dummy As HashSet(Of String) = Nothing
            _collapsedBy.TryRemove(menuId, dummy)
        End SyncLock
    End Sub

    Public Sub UpdateMenuBounds(menuId As String, newBoundingBox As ((Integer, Integer, Integer), (Integer, Integer, Integer)))
        SyncLock _overlapLock
            Dim info As ActiveMenuInfo = Nothing
            If _activeMenus.TryGetValue(menuId, info) Then
                info.BoundingBox = newBoundingBox
            End If
        End SyncLock
    End Sub

    Public Sub NotifyMenuShown(menuId As String)
        SyncLock _overlapLock
            Dim info As ActiveMenuInfo = Nothing
            If Not _activeMenus.TryGetValue(menuId, info) Then Return

            _showOrderCounter += 1
            info.ShowOrder = _showOrderCounter

            For Each kvp In _activeMenus
                If kvp.Key = menuId Then Continue For
                Dim other = kvp.Value
                If other.IsCollapsed Then Continue For
                If AreSameDropdown(menuId, kvp.Key) Then Continue For

                If AabbIntersects(info.BoundingBox, other.BoundingBox) Then
                    CollapseMenu(other.MenuId, menuId)
                End If
            Next
        End SyncLock
    End Sub

    Public Sub NotifyMenuBoundsChanged(menuId As String, newBoundingBox As ((Integer, Integer, Integer), (Integer, Integer, Integer)))
        SyncLock _overlapLock
            Dim info As ActiveMenuInfo = Nothing
            If Not _activeMenus.TryGetValue(menuId, info) Then Return

            Dim oldBounds = info.BoundingBox
            info.BoundingBox = newBoundingBox

            For Each kvp In _activeMenus
                If kvp.Key = menuId Then Continue For
                Dim other = kvp.Value
                If other.IsCollapsed Then Continue For
                If AreSameDropdown(menuId, kvp.Key) Then Continue For

                If AabbIntersects(newBoundingBox, other.BoundingBox) Then
                    CollapseMenu(other.MenuId, menuId)
                End If
            Next

            CheckAndRestoreNonOverlapping(menuId)
        End SyncLock
    End Sub

    Public Sub NotifyMenuHidden(menuId As String)
        SyncLock _overlapLock
            RestoreMenusCollapsedBy(menuId)
        End SyncLock
    End Sub

    Private Sub CollapseMenu(targetMenuId As String, causeMenuId As String)
        Dim info As ActiveMenuInfo = Nothing
        If Not _activeMenus.TryGetValue(targetMenuId, info) Then Return
        If info.IsCollapsed Then
            Dim causes = _collapsedBy.GetOrAdd(targetMenuId, Function(k) New HashSet(Of String)())
            SyncLock causes
                causes.Add(causeMenuId)
            End SyncLock
            Return
        End If

        info.IsCollapsed = True
        Dim causeSet = _collapsedBy.GetOrAdd(targetMenuId, Function(k) New HashSet(Of String)())
        SyncLock causeSet
            causeSet.Add(causeMenuId)
        End SyncLock

        Console.WriteLine($"[MenuSystem] Collapsing '{targetMenuId}' due to overlap with '{causeMenuId}'")
        info.CollapseAction?.Invoke()
    End Sub

    Private Sub RestoreMenusCollapsedBy(causeMenuId As String)
        Dim toRestore As New List(Of String)()

        For Each kvp In _collapsedBy
            Dim targetMenuId = kvp.Key
            Dim causes = kvp.Value
            SyncLock causes
                If causes.Remove(causeMenuId) Then
                    If causes.Count = 0 Then
                        toRestore.Add(targetMenuId)
                    End If
                End If
            End SyncLock
        Next

        For Each targetMenuId In toRestore
            Dim info As ActiveMenuInfo = Nothing
            If _activeMenus.TryGetValue(targetMenuId, info) Then
                info.IsCollapsed = False
                Console.WriteLine($"[MenuSystem] Restoring '{targetMenuId}'")
                info.RestoreAction?.Invoke()
            End If
            Dim dummy As HashSet(Of String) = Nothing
            _collapsedBy.TryRemove(targetMenuId, dummy)
        Next
    End Sub

    Private Sub CheckAndRestoreNonOverlapping(changedMenuId As String)
        Dim changedInfo As ActiveMenuInfo = Nothing
        If Not _activeMenus.TryGetValue(changedMenuId, changedInfo) Then Return

        Dim toCheck As New List(Of String)()
        Dim causes As HashSet(Of String) = Nothing

        For Each kvp In _collapsedBy
            Dim targetId = kvp.Key
            causes = kvp.Value
            SyncLock causes
                If causes.Contains(changedMenuId) Then
                    toCheck.Add(targetId)
                End If
            End SyncLock
        Next

        For Each targetId In toCheck
            Dim targetInfo As ActiveMenuInfo = Nothing
            If Not _activeMenus.TryGetValue(targetId, targetInfo) Then Continue For

            If Not AabbIntersects(changedInfo.BoundingBox, targetInfo.BoundingBox) Then
                If _collapsedBy.TryGetValue(targetId, causes) Then
                    SyncLock causes
                        causes.Remove(changedMenuId)
                        If causes.Count = 0 Then
                            targetInfo.IsCollapsed = False
                            Console.WriteLine($"[MenuSystem] Restoring '{targetId}' (no longer overlapping)")
                            targetInfo.RestoreAction?.Invoke()
                            Dim dummy As HashSet(Of String) = Nothing
                            _collapsedBy.TryRemove(targetId, dummy)
                        End If
                    End SyncLock
                End If
            End If
        Next
    End Sub

    Private Function AabbIntersects(a As ((Integer, Integer, Integer), (Integer, Integer, Integer)),
                                     b As ((Integer, Integer, Integer), (Integer, Integer, Integer))) As Boolean
        Dim aMin = a.Item1, aMax = a.Item2
        Dim bMin = b.Item1, bMax = b.Item2

        If aMax.Item1 < bMin.Item1 OrElse aMin.Item1 > bMax.Item1 Then Return False
        If aMax.Item2 < bMin.Item2 OrElse aMin.Item2 > bMax.Item2 Then Return False
        If aMax.Item3 < bMin.Item3 OrElse aMin.Item3 > bMax.Item3 Then Return False

        Return True
    End Function

    Private Function AreSameDropdown(menuIdA As String, menuIdB As String) As Boolean
        If menuIdA.StartsWith("Dropdown_") AndAlso menuIdB.StartsWith("Dropdown_") Then
            Dim baseA = menuIdA.Substring(9)
            Dim baseB = menuIdB.Substring(9)
            Dim nameA = If(baseA.EndsWith("_Host"), baseA.Substring(0, baseA.Length - 5),
                        If(baseA.EndsWith("_Items"), baseA.Substring(0, baseA.Length - 6), baseA))
            Dim nameB = If(baseB.EndsWith("_Host"), baseB.Substring(0, baseB.Length - 5),
                        If(baseB.EndsWith("_Items"), baseB.Substring(0, baseB.Length - 6), baseB))
            Return nameA = nameB
        End If
        Return False
    End Function

    ' ====================
    ' == CONTEXT MENU SELECTION HANDLER
    ' ====================

    Private Sub OnContextMenuItemSelected(menuName As String, itemName As String,
                                          itemIndex As Integer, parentItemName As String)
        If _aggregator Is Nothing Then Return
        Dim payload = New With {
            .MenuName = menuName,
            .ItemName = itemName,
            .ItemIndex = itemIndex,
            .ParentItem = parentItemName
        }
        Try
            CallByName(_aggregator, "Publish", CallType.Method, "MenuItemSelected", payload)
        Catch : End Try
    End Sub

    ' ====================
    ' == MENU LIFECYCLE
    ' ====================

    Public Sub CreateMenu(menuName As String, panel As PanelType,
                          Optional anchorRow As Integer = 5,
                          Optional anchorCol As Integer = 10,
                          Optional hostCharWidth As Integer = -1,
                          Optional itemCharWidth As Integer = -1)
        If String.IsNullOrWhiteSpace(menuName) Then
            Console.WriteLine("[MenuSystem] Cannot create menu with empty name.")
            Return
        End If
        If _menus.ContainsKey(menuName) Then
            Console.WriteLine($"[MenuSystem] Menu '{menuName}' already exists.")
            Return
        End If

        Dim hcw = If(hostCharWidth > 0, hostCharWidth, DefaultDropdownHostCharWidth)
        Dim icw = If(itemCharWidth > 0, itemCharWidth, DefaultDropdownItemCharWidth)

        Dim instance As New MenuInstance(menuName, panel, anchorRow, anchorCol, _api, _aggregator, _contextMenu, Me, hcw, icw)
        If _menus.TryAdd(menuName, instance) Then
            instance.Initialize()
            Console.WriteLine($"[MenuSystem] Created menu '{menuName}' (host={hcw}, item={icw}).")
        End If
    End Sub

    Public Sub DeleteMenu(menuName As String)
        Dim instance As MenuInstance = Nothing
        If Not _menus.TryRemove(menuName, instance) Then
            Console.WriteLine($"[MenuSystem] Menu '{menuName}' doesn't exist.")
            Return
        End If
        instance.Dispose()
        Console.WriteLine($"[MenuSystem] Deleted menu '{menuName}'.")
    End Sub

    ' ====================
    ' == CONTEXT MENU API
    ' ====================

    Public Sub ShowContextMenu(panel As PanelType, clickRow As Integer, clickCol As Integer,
                               items() As String,
                               Optional charWidth As Integer = -1,
                               Optional autoWidth As Boolean? = Nothing)
        If _contextMenu Is Nothing Then
            Console.WriteLine("[MenuSystem] Context menu not initialized.")
            Return
        End If
        Dim cw = If(charWidth > 0, charWidth, DefaultContextCharWidth)
        Dim aw = If(autoWidth.HasValue, autoWidth.Value, DefaultContextAutoWidth)
        _contextMenu.Show(panel, clickRow, clickCol, items, cw, aw)
    End Sub

    Public Sub HideContextMenu()
        _contextMenu?.Hide()
    End Sub

    Public Sub DefineContextSubmenu(parentItemName As String, childItems() As String)
        _contextMenu?.DefineSubmenu(parentItemName, childItems)
    End Sub

    Public Sub ClearContextSubmenus()
        _contextMenu?.ClearSubmenuDefinitions()
    End Sub

    Public Sub DefineMenuSubmenu(menuName As String, parentItemName As String, childItems() As String)
        Dim instance = GetMenuInstance(menuName)
        instance?.DefineSubmenu(parentItemName, childItems)
    End Sub

    Public Function IsContextMenu(menuName As String) As Boolean
        Return menuName = "__ContextMenu__"
    End Function

    ' ====================
    ' == ITEM MANAGEMENT
    ' ====================

    Public Sub AddMenuItem(menuName As String, itemName As String)
        Dim instance = GetMenuInstance(menuName)
        If instance Is Nothing Then Return
        If String.IsNullOrWhiteSpace(itemName) Then
            Console.WriteLine($"[MenuSystem] Cannot add empty item.")
            Return
        End If
        instance.AddItem(itemName)
        Console.WriteLine($"[MenuSystem] Added '{itemName}' to '{menuName}'.")
    End Sub

    Public Sub RemoveMenuItem(menuName As String, itemName As String)
        Dim instance = GetMenuInstance(menuName)
        If instance Is Nothing Then Return
        If instance.RemoveItem(itemName) Then
            Console.WriteLine($"[MenuSystem] Removed '{itemName}' from '{menuName}'.")
        Else
            Console.WriteLine($"[MenuSystem] Item '{itemName}' not found in '{menuName}'.")
        End If
    End Sub

    Public Sub ClearMenu(menuName As String)
        Dim instance = GetMenuInstance(menuName)
        If instance Is Nothing Then Return
        instance.ClearItems()
        Console.WriteLine($"[MenuSystem] Cleared '{menuName}'.")
    End Sub

    ' ====================
    ' == QUERY METHODS
    ' ====================

    Public Function GetMenuItems(menuName As String) As String()
        Dim instance = GetMenuInstance(menuName)
        If instance Is Nothing Then Return New String() {}
        Return instance.GetItemNamesInOrder().ToArray()
    End Function

    Public Function GetMenuItemCount(menuName As String) As Integer
        Dim instance = GetMenuInstance(menuName)
        If instance Is Nothing Then Return 0
        Return instance.GetItemCount()
    End Function

    ' ====================
    ' == INTRA-MENU REORDER
    ' ====================

    Public Sub MoveItemUp(menuName As String, itemName As String)
        Dim instance = GetMenuInstance(menuName)
        If instance Is Nothing Then Return

        Dim items = instance.GetItemNamesInOrder()
        Dim idx = items.IndexOf(itemName)
        If idx < 0 Then
            Console.WriteLine($"[MenuSystem] Item '{itemName}' not found in '{menuName}'.")
            Return
        End If
        If idx = 0 Then
            Console.WriteLine($"[MenuSystem] Item '{itemName}' already at top.")
            Return
        End If

        Dim newOrder = New List(Of String)(items)
        newOrder.RemoveAt(idx)
        newOrder.Insert(idx - 1, itemName)
        instance.ReorderItems(newOrder)
        PublishItemMoved(menuName, menuName, itemName, idx, idx - 1)
    End Sub

    Public Sub MoveItemDown(menuName As String, itemName As String)
        Dim instance = GetMenuInstance(menuName)
        If instance Is Nothing Then Return

        Dim items = instance.GetItemNamesInOrder()
        Dim idx = items.IndexOf(itemName)
        If idx < 0 Then
            Console.WriteLine($"[MenuSystem] Item '{itemName}' not found in '{menuName}'.")
            Return
        End If
        If idx >= items.Count - 1 Then
            Console.WriteLine($"[MenuSystem] Item '{itemName}' already at bottom.")
            Return
        End If

        Dim newOrder = New List(Of String)(items)
        newOrder.RemoveAt(idx)
        newOrder.Insert(idx + 1, itemName)
        instance.ReorderItems(newOrder)
        PublishItemMoved(menuName, menuName, itemName, idx, idx + 1)
    End Sub

    Public Sub MoveItemToIndex(menuName As String, itemName As String, newIndex As Integer)
        Dim instance = GetMenuInstance(menuName)
        If instance Is Nothing Then Return

        Dim items = instance.GetItemNamesInOrder()
        Dim idx = items.IndexOf(itemName)
        If idx < 0 Then
            Console.WriteLine($"[MenuSystem] Item '{itemName}' not found in '{menuName}'.")
            Return
        End If
        If newIndex < 0 OrElse newIndex >= items.Count Then
            Console.WriteLine($"[MenuSystem] Index {newIndex} out of range.")
            Return
        End If
        If idx = newIndex Then Return

        Dim newOrder = New List(Of String)(items)
        newOrder.RemoveAt(idx)
        newOrder.Insert(newIndex, itemName)
        instance.ReorderItems(newOrder)
        PublishItemMoved(menuName, menuName, itemName, idx, newIndex)
    End Sub

    Public Sub SortMenuItems(menuName As String, sortOrder As MenuSortOrder)
        Dim instance = GetMenuInstance(menuName)
        If instance Is Nothing Then Return

        Dim items = instance.GetItemNamesInOrder()
        Dim sorted As List(Of String)

        Select Case sortOrder
            Case MenuSortOrder.Alphabetical
                sorted = items.OrderBy(Function(s) s).ToList()
            Case MenuSortOrder.ReverseAlphabetical
                sorted = items.OrderByDescending(Function(s) s).ToList()
            Case Else
                Return
        End Select

        instance.ReorderItems(sorted)
        PublishItemsReordered(menuName, sorted)
    End Sub

    ' ====================
    ' == INTER-MENU TRANSFER
    ' ====================

    Public Sub MoveItemToMenu(sourceMenu As String, itemName As String, targetMenu As String)
        MoveItemToMenuAt(sourceMenu, itemName, targetMenu, -1)
    End Sub

    Public Sub MoveItemToMenuAt(sourceMenu As String, itemName As String,
                                 targetMenu As String, targetIndex As Integer)
        Dim srcInstance = GetMenuInstance(sourceMenu)
        Dim tgtInstance = GetMenuInstance(targetMenu)
        If srcInstance Is Nothing OrElse tgtInstance Is Nothing Then Return

        Dim srcItems = srcInstance.GetItemNamesInOrder()
        Dim srcIdx = srcItems.IndexOf(itemName)
        If srcIdx < 0 Then
            Console.WriteLine($"[MenuSystem] Item '{itemName}' not found in '{sourceMenu}'.")
            Return
        End If

        srcInstance.RemoveItem(itemName)

        Dim tgtItems = tgtInstance.GetItemNamesInOrder()
        Dim insertIdx As Integer
        If targetIndex < 0 OrElse targetIndex >= tgtItems.Count Then
            insertIdx = tgtItems.Count
            tgtInstance.AddItem(itemName)
        Else
            insertIdx = targetIndex
            tgtItems.Insert(targetIndex, itemName)
            tgtInstance.ReorderItems(tgtItems)
        End If

        PublishItemMoved(sourceMenu, targetMenu, itemName, srcIdx, insertIdx)
        Console.WriteLine($"[MenuSystem] Moved '{itemName}' from '{sourceMenu}' to '{targetMenu}'.")
    End Sub

    Public Sub CopyItemToMenu(sourceMenu As String, itemName As String, targetMenu As String)
        Dim srcInstance = GetMenuInstance(sourceMenu)
        Dim tgtInstance = GetMenuInstance(targetMenu)
        If srcInstance Is Nothing OrElse tgtInstance Is Nothing Then Return

        Dim srcItems = srcInstance.GetItemNamesInOrder()
        If Not srcItems.Contains(itemName) Then
            Console.WriteLine($"[MenuSystem] Item '{itemName}' not found in '{sourceMenu}'.")
            Return
        End If

        tgtInstance.AddItem(itemName)
        Console.WriteLine($"[MenuSystem] Copied '{itemName}' from '{sourceMenu}' to '{targetMenu}'.")
    End Sub

    ' ====================
    ' == HELPERS
    ' ====================

    Private Function GetMenuInstance(menuName As String) As MenuInstance
        If String.IsNullOrWhiteSpace(menuName) Then Return Nothing
        Dim instance As MenuInstance = Nothing
        If Not _menus.TryGetValue(menuName, instance) Then
            Console.WriteLine($"[MenuSystem] Menu '{menuName}' doesn't exist.")
            Return Nothing
        End If
        Return instance
    End Function

    Private Sub PublishItemMoved(srcMenu As String, tgtMenu As String,
                                  itemName As String, oldIdx As Integer, newIdx As Integer)
        If _aggregator Is Nothing Then Return
        Dim payload = New With {
            .SourceMenu = srcMenu,
            .TargetMenu = tgtMenu,
            .ItemName = itemName,
            .OldIndex = oldIdx,
            .NewIndex = newIdx
        }
        Try
            CallByName(_aggregator, "Publish", CallType.Method, "MenuItemMoved", payload)
        Catch : End Try
    End Sub

    Private Sub PublishItemsReordered(menuName As String, newOrder As List(Of String))
        If _aggregator Is Nothing Then Return
        Dim payload = New With {
            .MenuName = menuName,
            .NewOrder = newOrder.ToArray()
        }
        Try
            CallByName(_aggregator, "Publish", CallType.Method, "MenuItemsReordered", payload)
        Catch : End Try
    End Sub

    Public Function IsClickInsideAnyMenu(panel As Object, row As Integer, col As Integer) As Boolean
        For Each kvp In _menus
            If kvp.Value.HitTest(panel, row, col) Then Return True
        Next
        Return _contextMenu IsNot Nothing AndAlso _contextMenu.HitTest(panel, row, col)
    End Function

#Region "CLICK OWNERSHIP (see ClickOwnership.vb)"
    ' THESE FIVE NAMES ARE THE PUBLISHED FACE: MenuSystemApi
    ' mirrors them, and foreign plugins bind to them BY STRING through
    ' PluginHub.Exec. The implementation beneath carries DIFFERENT names on purpose,
    ' so a developer browsing this plugin's callable surface meets one name per idea.
    ' NEVER OVERLOAD ANY OF THESE: PluginHub.Exec uses GetType().GetMethod(name).
    Public Function ResolveClickOwner(evt As Object) As String
        If _clickOwner Is Nothing Then Return ""
        Return _clickOwner.ZoneIdOwningClick(evt)
    End Function

    Public Function IsClickOwned(evt As Object) As Boolean
        Return ResolveClickOwner(evt) <> ""
    End Function

    Public Function IsClickOwnedBy(evt As Object, zoneId As String) As Boolean
        If String.IsNullOrEmpty(zoneId) Then Return False
        Return ResolveClickOwner(evt) = zoneId
    End Function

    Public Function ReportClickOwnership(evt As Object) As Dictionary(Of String, Object)
        If _clickOwner Is Nothing Then Return New Dictionary(Of String, Object)
        Return _clickOwner.OwnershipDiagnostics(evt)
    End Function

    ' The menu system's OWN ownership decision, by ray. IsClickInsideAnyMenu remains
    ' as it is for the cell-shaped legacy caller in CadHandPlugin.
    Public Function IsClickOwnedByMenu(evt As Object) As Boolean
        Dim owner = ResolveClickOwner(evt)
        If owner = "" Then Return False
        For Each kvp In _menus
            If kvp.Value.DropdownOwnsZone(owner) Then Return True
        Next
        Return _contextMenu IsNot Nothing AndAlso _contextMenu.ContextOwnsZone(owner)
    End Function
#End Region

End Class

Public Enum MenuSortOrder
    Alphabetical
    ReverseAlphabetical
End Enum

Public Class ActiveMenuInfo
    Public Property MenuId As String
    Public Property BoundingBox As ((Integer, Integer, Integer), (Integer, Integer, Integer))
    Public Property IsCollapsed As Boolean
    Public Property CollapseAction As Action
    Public Property RestoreAction As Action
    Public Property ShowOrder As Long
End Class