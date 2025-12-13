Imports System.Collections.Concurrent
Imports System.Threading
Imports Current.PluginApi

<PluginMetadata("Menu System", "1.0", "Nasuno", "Provides a standardized menu system for creating interactive dropdown menus in the spatial environment.")>
Public Class MenuSystemPlugin
    Implements IPlugin

    Private _api As ICurrentApi
    Private _aggregator As Object
    Private ReadOnly _menus As New ConcurrentDictionary(Of String, MenuInstance)()

    Public Sub Execute(api As ICurrentApi) Implements IPlugin.Execute
        _api = api
        PluginLocator.Register("MenuSystem", Me)
        Console.WriteLine("[MenuSystem] Registered as global menu system.")

        _aggregator = PluginLocator.Get(Of Object)("EventAggregator")
        If _aggregator Is Nothing Then
            Console.WriteLine("[MenuSystem] Warning: EventAggregator not found. Hover events will not work.")
        Else
            Console.WriteLine("[MenuSystem] Found EventAggregator. Hover events enabled.")
        End If
    End Sub

    Public Sub CreateMenu(menuName As String, panel As PanelType, Optional anchorTopRow As Integer = 5, Optional anchorLeftCol As Integer = 10)
        If String.IsNullOrWhiteSpace(menuName) Then
            Console.WriteLine("[MenuSystem] Cannot create menu with empty name.")
            Return
        End If

        If _menus.ContainsKey(menuName) Then
            Console.WriteLine($"[MenuSystem] Menu '{menuName}' already exists.")
            Return
        End If

        Dim instance As New MenuInstance(menuName, panel, anchorTopRow, anchorLeftCol, _api, _aggregator)
        If _menus.TryAdd(menuName, instance) Then
            instance.Initialize()
            Console.WriteLine($"[MenuSystem] Created menu '{menuName}'.")
        End If
    End Sub

    Public Sub AddMenuItem(menuName As String, itemText As String)
        Dim instance As MenuInstance = Nothing
        If Not _menus.TryGetValue(menuName, instance) Then
            Console.WriteLine($"[MenuSystem] Menu '{menuName}' doesn't exist.")
            Return
        End If

        If String.IsNullOrWhiteSpace(itemText) Then
            Console.WriteLine($"[MenuSystem] Cannot add empty item to menu '{menuName}'.")
            Return
        End If

        instance.AddItem(itemText)
        Console.WriteLine($"[MenuSystem] Added item '{itemText}' to menu '{menuName}'.")
    End Sub

    Public Sub RemoveMenuItem(menuName As String, itemText As String)
        Dim instance As MenuInstance = Nothing
        If Not _menus.TryGetValue(menuName, instance) Then
            Console.WriteLine($"[MenuSystem] Menu '{menuName}' doesn't exist.")
            Return
        End If

        If instance.RemoveItem(itemText) Then
            Console.WriteLine($"[MenuSystem] Removed item '{itemText}' from menu '{menuName}'.")
        Else
            Console.WriteLine($"[MenuSystem] Item '{itemText}' not found in menu '{menuName}'.")
        End If
    End Sub

    Public Sub ClearMenu(menuName As String)
        Dim instance As MenuInstance = Nothing
        If Not _menus.TryGetValue(menuName, instance) Then
            Console.WriteLine($"[MenuSystem] Menu '{menuName}' doesn't exist.")
            Return
        End If

        instance.ClearItems()
        Console.WriteLine($"[MenuSystem] Cleared menu '{menuName}'.")
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

End Class


Public Class MenuInstance
    Implements IDisposable

    Private Const CharWidth As Integer = 5
    Private Const CharHeight As Integer = 7
    Private Const Gutter As Integer = 1
    Private Const HostCharCount As Integer = 7
    Private Const ItemCharCount As Integer = 8

    Private ReadOnly _menuName As String
    Private ReadOnly _panel As PanelType
    Private ReadOnly _anchorRow As Integer
    Private ReadOnly _anchorCol As Integer
    Private ReadOnly _api As ICurrentApi
    Private ReadOnly _aggregator As Object

    Private ReadOnly _hostZoneId As String
    Private ReadOnly _hostMarginSetName As String

    Private ReadOnly _items As New List(Of DropdownItem)()
    Private ReadOnly _itemsByText As New Dictionary(Of String, DropdownItem)()

    Private ReadOnly _hoveredZones As New ConcurrentDictionary(Of String, Boolean)()
    Private ReadOnly _sessionLock As New Object()

    Private _isExpanded As Boolean = False
    Private _disposed As Boolean = False

    Private _enterCallback As Action(Of Object)
    Private _leaveCallback As Action(Of Object)

    Public Sub New(menuName As String, panel As PanelType, anchorRow As Integer, anchorCol As Integer, api As ICurrentApi, aggregator As Object)
        _menuName = menuName
        _panel = panel
        _anchorRow = anchorRow
        _anchorCol = anchorCol
        _api = api
        _aggregator = aggregator

        _hostZoneId = $"MenuHost_{menuName}"
        _hostMarginSetName = $"HostSet_{menuName}"
    End Sub

    Public Sub Initialize()
        CreateHostZone()
        RegisterForMouseEvents()
        SubscribeToEvents()
    End Sub

    Private Sub CreateHostZone()
        Dim insideW = HostCharCount * CharWidth + (HostCharCount + 1) * Gutter
        Dim insideH = CharHeight + 2 * Gutter

        Dim leftCol = _anchorCol
        Dim rightCol = leftCol + insideW + 1
        Dim topRow = _anchorRow
        Dim bottomRow = topRow + insideH + 1

        Dim topId = $"{_hostMarginSetName}_T"
        Dim bottomId = $"{_hostMarginSetName}_B"
        Dim leftId = $"{_hostMarginSetName}_L"
        Dim rightId = $"{_hostMarginSetName}_R"

        _api.CreateMargin(topId, MarginType.RowMargin, _panel, topRow, Nothing, False)
        _api.CreateMargin(bottomId, MarginType.RowMargin, _panel, bottomRow, Nothing, False)
        _api.CreateMargin(leftId, MarginType.ColumnMargin, _panel, Nothing, leftCol, False)
        _api.CreateMargin(rightId, MarginType.ColumnMargin, _panel, Nothing, rightCol, False)

        _api.CreateMarginSet(_hostMarginSetName, topId, bottomId, leftId, rightId)

        _api.CreateSpatialZone(_hostZoneId)
        _api.AssignZoneMarginSetA(_hostZoneId, _hostMarginSetName)
        _api.SwitchZoneToMarginSetA(_hostZoneId)

        Dim zone = _api.GetSpatialZone(_hostZoneId)
        If zone IsNot Nothing Then
            zone.Text = PadText(_menuName, HostCharCount)
        End If
    End Sub

    Private Sub RegisterForMouseEvents()
        If _aggregator Is Nothing Then Return

        Dim hostZone = _api.GetSpatialZone(_hostZoneId)
        If hostZone Is Nothing Then Return

        Try
            CallByName(_aggregator, "RegisterZoneForMouseEvents", CallType.Method, hostZone)
            Console.WriteLine($"[MenuInstance] Registered host zone '{_hostZoneId}' for mouse events.")
        Catch ex As Exception
            Console.WriteLine($"[MenuInstance] Failed to register host zone for mouse events: {ex.Message}")
        End Try
    End Sub

    Private Sub SubscribeToEvents()
        If _aggregator Is Nothing Then Return

        _enterCallback = Sub(evt As Object)
                             Dim zoneId = SafeGetStringProperty(evt, "ZoneId")
                             OnZoneEnter(zoneId)
                         End Sub

        _leaveCallback = Sub(evt As Object)
                             Dim zoneId = SafeGetStringProperty(evt, "ZoneId")
                             OnZoneLeave(zoneId)
                         End Sub

        Try
            CallByName(_aggregator, "Subscribe", CallType.Method, "SpatialZoneMouseEnter", _enterCallback)
            CallByName(_aggregator, "Subscribe", CallType.Method, "SpatialZoneMouseLeave", _leaveCallback)
            Console.WriteLine($"[MenuInstance] Subscribed to mouse events for menu '{_menuName}'.")
        Catch ex As Exception
            Console.WriteLine($"[MenuInstance] Failed to subscribe to events: {ex.Message}")
        End Try
    End Sub

    Private Sub UnsubscribeFromEvents()
        If _aggregator Is Nothing Then Return

        Try
            If _enterCallback IsNot Nothing Then
                CallByName(_aggregator, "Unsubscribe", CallType.Method, "SpatialZoneMouseEnter", _enterCallback)
            End If
            If _leaveCallback IsNot Nothing Then
                CallByName(_aggregator, "Unsubscribe", CallType.Method, "SpatialZoneMouseLeave", _leaveCallback)
            End If
        Catch ex As Exception
            Console.WriteLine($"[MenuInstance] Failed to unsubscribe from events: {ex.Message}")
        End Try
    End Sub

    Private Sub UnregisterFromMouseEvents()
        If _aggregator Is Nothing Then Return

        Dim hostZone = _api.GetSpatialZone(_hostZoneId)
        If hostZone IsNot Nothing Then
            Try
                CallByName(_aggregator, "UnregisterZoneForMouseEvents", CallType.Method, hostZone)
            Catch ex As Exception
                Console.WriteLine($"[MenuInstance] Failed to unregister host zone: {ex.Message}")
            End Try
        End If

        For Each item In _items
            Dim itemZone = _api.GetSpatialZone(item.ZoneId)
            If itemZone IsNot Nothing Then
                Try
                    CallByName(_aggregator, "UnregisterZoneForMouseEvents", CallType.Method, itemZone)
                Catch ex As Exception
                End Try
            End If
        Next
    End Sub

    Public Sub AddItem(itemText As String)
        If _itemsByText.ContainsKey(itemText) Then
            Console.WriteLine($"[MenuInstance] Item '{itemText}' already exists in menu '{_menuName}'.")
            Return
        End If

        If _isExpanded Then
            Collapse()
        End If

        Dim itemIndex = _items.Count
        Dim item As New DropdownItem(_menuName, itemIndex, itemText)

        _items.Add(item)
        _itemsByText(itemText) = item

        CreateItemZone(item)
    End Sub

    Private Sub CreateItemZone(item As DropdownItem)
        Dim hostZone = _api.GetSpatialZone(_hostZoneId)
        If hostZone Is Nothing Then Return

        Dim hostBottom = hostZone.Bottom

        Dim insideW = ItemCharCount * CharWidth + (ItemCharCount + 1) * Gutter
        Dim insideH = CharHeight + 2 * Gutter

        Dim leftCol = _anchorCol
        Dim rightCol = leftCol + insideW + 1

        Dim expandedTopRow = hostBottom + (item.Index * (insideH + 1))
        Dim expandedBottomRow = expandedTopRow + insideH + 1

        Dim expandedSetName = $"Expanded_{_menuName}_{item.Index}"
        Dim exTopId = $"{expandedSetName}_T"
        Dim exBottomId = $"{expandedSetName}_B"
        Dim exLeftId = $"{expandedSetName}_L"
        Dim exRightId = $"{expandedSetName}_R"

        _api.CreateMargin(exTopId, MarginType.RowMargin, _panel, expandedTopRow, Nothing, False)
        _api.CreateMargin(exBottomId, MarginType.RowMargin, _panel, expandedBottomRow, Nothing, False)
        _api.CreateMargin(exLeftId, MarginType.ColumnMargin, _panel, Nothing, leftCol, False)
        _api.CreateMargin(exRightId, MarginType.ColumnMargin, _panel, Nothing, rightCol, False)
        _api.CreateMarginSet(expandedSetName, exTopId, exBottomId, exLeftId, exRightId)

        Dim collapsedSetName = $"Collapsed_{_menuName}_{item.Index}"
        Dim colTopId = $"{collapsedSetName}_T"
        Dim colBottomId = $"{collapsedSetName}_B"
        Dim colLeftId = $"{collapsedSetName}_L"
        Dim colRightId = $"{collapsedSetName}_R"

        _api.CreateMargin(colTopId, MarginType.RowMargin, _panel, hostBottom, Nothing, False)
        _api.CreateMargin(colBottomId, MarginType.RowMargin, _panel, hostBottom, Nothing, False)
        _api.CreateMargin(colLeftId, MarginType.ColumnMargin, _panel, Nothing, leftCol, False)
        _api.CreateMargin(colRightId, MarginType.ColumnMargin, _panel, Nothing, rightCol, False)
        _api.CreateMarginSet(collapsedSetName, colTopId, colBottomId, colLeftId, colRightId)

        item.ExpandedSetName = expandedSetName
        item.CollapsedSetName = collapsedSetName

        _api.CreateSpatialZone(item.ZoneId)
        _api.AssignZoneMarginSetA(item.ZoneId, collapsedSetName)
        _api.AssignZoneMarginSetB(item.ZoneId, expandedSetName)
        _api.SwitchZoneToMarginSetA(item.ZoneId)

        Dim zone = _api.GetSpatialZone(item.ZoneId)
        If zone IsNot Nothing Then
            zone.Text = PadText(item.Text, ItemCharCount)
        End If
    End Sub

    Public Function RemoveItem(itemText As String) As Boolean
        Dim item As DropdownItem = Nothing
        If Not _itemsByText.TryGetValue(itemText, item) Then
            Return False
        End If

        If _isExpanded Then
            Collapse()
        End If

        Dim itemZone = _api.GetSpatialZone(item.ZoneId)
        If itemZone IsNot Nothing AndAlso _aggregator IsNot Nothing Then
            Try
                CallByName(_aggregator, "UnregisterZoneForMouseEvents", CallType.Method, itemZone)
            Catch ex As Exception
            End Try
        End If

        _itemsByText.Remove(itemText)
        _items.Remove(item)

        Try
            _api.RemoveSpatialZone(item.ZoneId)
        Catch ex As Exception
            Console.WriteLine($"[MenuInstance] Failed to remove zone: {ex.Message}")
        End Try

        Return True
    End Function

    Public Sub ClearItems()
        If _isExpanded Then
            Collapse()
        End If

        For Each item In _items.ToList()
            Dim itemZone = _api.GetSpatialZone(item.ZoneId)
            If itemZone IsNot Nothing AndAlso _aggregator IsNot Nothing Then
                Try
                    CallByName(_aggregator, "UnregisterZoneForMouseEvents", CallType.Method, itemZone)
                Catch ex As Exception
                End Try
            End If

            Try
                _api.RemoveSpatialZone(item.ZoneId)
            Catch ex As Exception
                Console.WriteLine($"[MenuInstance] Failed to remove zone: {ex.Message}")
            End Try
        Next

        _items.Clear()
        _itemsByText.Clear()
    End Sub

    Private Function IsMyZone(zoneId As String) As Boolean
        If zoneId = _hostZoneId Then Return True
        For Each item In _items
            If item.ZoneId = zoneId Then Return True
        Next
        Return False
    End Function

    Private Sub OnZoneEnter(zoneId As String)
        If Not IsMyZone(zoneId) Then Return

        _hoveredZones(zoneId) = True

        If zoneId = _hostZoneId Then
            Console.WriteLine($"[MenuInstance] ENTER host zone '{_menuName}'")
            If Not _isExpanded Then
                Expand()
            End If
        Else
            Dim item = _items.FirstOrDefault(Function(i) i.ZoneId = zoneId)
            If item IsNot Nothing Then
                Console.WriteLine($"[MenuInstance] ENTER menu item '{item.Text}' in menu '{_menuName}'")
            End If
        End If
    End Sub

    Private Sub OnZoneLeave(zoneId As String)
        If Not IsMyZone(zoneId) Then Return

        Dim dummy As Boolean
        _hoveredZones.TryRemove(zoneId, dummy)

        If zoneId = _hostZoneId Then
            Console.WriteLine($"[MenuInstance] LEAVE host zone '{_menuName}'")
        Else
            Dim item = _items.FirstOrDefault(Function(i) i.ZoneId = zoneId)
            If item IsNot Nothing Then
                Console.WriteLine($"[MenuInstance] LEAVE menu item '{item.Text}' in menu '{_menuName}'")
            End If
        End If

        ScheduleCollapseCheck()
    End Sub

    Private Sub ScheduleCollapseCheck()
        Dim checkThread As New Thread(
            Sub()
                Thread.Sleep(50)
                SyncLock _sessionLock
                    Dim anyHovered = False
                    For Each kvp In _hoveredZones
                        If kvp.Value Then
                            anyHovered = True
                            Exit For
                        End If
                    Next

                    If Not anyHovered AndAlso _isExpanded Then
                        Collapse()
                    End If
                End SyncLock
            End Sub) With {.IsBackground = True}
        checkThread.Start()
    End Sub

    Private Sub Expand()
        If _isExpanded Then Return
        If _items.Count = 0 Then Return

        For Each item In _items
            _api.SwapZoneMarginSets(item.ZoneId)

            Dim itemZone = _api.GetSpatialZone(item.ZoneId)
            If itemZone IsNot Nothing AndAlso _aggregator IsNot Nothing Then
                Try
                    CallByName(_aggregator, "RegisterZoneForMouseEvents", CallType.Method, itemZone)
                Catch ex As Exception
                End Try
            End If
        Next

        _isExpanded = True
        Console.WriteLine($"[MenuInstance] Expanded menu '{_menuName}'")
    End Sub

    Private Sub Collapse()
        If Not _isExpanded Then Return

        For Each item In _items
            Dim itemZone = _api.GetSpatialZone(item.ZoneId)
            If itemZone IsNot Nothing AndAlso _aggregator IsNot Nothing Then
                Try
                    CallByName(_aggregator, "UnregisterZoneForMouseEvents", CallType.Method, itemZone)
                Catch ex As Exception
                End Try
            End If

            _api.SwapZoneMarginSets(item.ZoneId)
        Next

        _isExpanded = False
        Console.WriteLine($"[MenuInstance] Collapsed menu '{_menuName}'")
    End Sub

    Private Function PadText(text As String, charCount As Integer) As String
        If text.Length >= charCount Then
            Return text.Substring(0, charCount)
        End If
        Return text & New String(" "c, charCount - text.Length)
    End Function

    Private Function SafeGetStringProperty(obj As Object, propName As String) As String
        If obj Is Nothing Then Return ""
        Try
            Dim value = CallByName(obj, propName, CallType.Get)
            If value Is Nothing Then Return ""
            Return CStr(value)
        Catch
            Return ""
        End Try
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True

        If _isExpanded Then
            Collapse()
        End If

        UnsubscribeFromEvents()
        UnregisterFromMouseEvents()
        ClearItems()

        Try
            _api.RemoveSpatialZone(_hostZoneId)
        Catch ex As Exception
            Console.WriteLine($"[MenuInstance] Failed to remove host zone: {ex.Message}")
        End Try
    End Sub

End Class


Public Class DropdownItem
    Public ReadOnly Property MenuName As String
    Public ReadOnly Property Index As Integer
    Public ReadOnly Property Text As String
    Public ReadOnly Property ZoneId As String

    Public Property ExpandedSetName As String
    Public Property CollapsedSetName As String

    Public Sub New(menuName As String, index As Integer, text As String)
        Me.MenuName = menuName
        Me.Index = index
        Me.Text = text
        Me.ZoneId = $"DropdownZone_{menuName}_{index}"
    End Sub
End Class