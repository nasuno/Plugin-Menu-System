' FILE: MenuSystemApi.vb
' NAMESPACE: DropdownMenuProvider (assumed - same as MenuSystemPlugin)
' DEPENDENCY: Requires MenuSystemPlugin.vb (defines MenuSortOrder enum, MenuSystemPlugin class)
' =============================================================================
'
' PURPOSE
' Facade exposing MenuSystemPlugin client surface while hiding IPlugin cruft and 
' spatial overlap internals (AABB collision, Z-order resolution, registration counters).
'
' ARCHITECTURE SUMMARY
' Dual-mode persistence model:
'   1. Named Dropdowns: Persistent MenuInstance objects keyed in ConcurrentDictionary.
'      Explicit lifecycle (CreateMenu/DeleteMenu). Automatic spatial registration.
'   2. Context Menu: Singleton-pooled ContextMenuManager. Ephemeral, recycled per use.
'
' EVENT CONTRACT (Dynamic/Loose Coupling)
' All operations publish via EventAggregator (CallByName). Payloads are anonymous objects.
'   - MenuItemSelected:     {MenuName, ItemName, ItemIndex, ParentItem}
'   - MenuItemMoved:        {SourceMenu, TargetMenu, ItemName, OldIndex, NewIndex}
'   - MenuItemsReordered:   {MenuName, NewOrder()}
'
' STATE SEMANTICS
'   - Atomic replacement: Reorder operations commit entire sequences, never deltas.
'   - Value semantics: Items are strings (copies), not references or IDs.
'   - Thread-safe reads: ConcurrentDictionary allows parallel GetMenuItems.
'
' NULL SAFETY
' All methods check _plugin existence (?.) before delegation. Return empty/false rather 
' than throwing if plugin unavailable.
' =============================================================================
Imports System.Collections.Concurrent
Imports Current.PluginApi

Namespace DropdownMenuProvider

    Public Class MenuSystemApi

        ' PluginHub.Fetch replaces PluginLocator.Get; identical lookup semantics, Nothing if unregistered.
        Private Shared ReadOnly _plugin As MenuSystemPlugin = PluginHub.Fetch(Of MenuSystemPlugin)("MenuSystem")

        ' =========================================================================
        ' GLOBAL CONFIGURATION (Static Defaults)
        ' =========================================================================
        ' Affect all subsequent instantiations. Modify before CreateMenu/ShowContextMenu.
        ' Maps directly to MenuSystemPlugin Shared fields.

        Public Shared Property DefaultContextCharWidth As Integer
            Get
                Return MenuSystemPlugin.DefaultContextCharWidth
            End Get
            Set(value As Integer)
                MenuSystemPlugin.DefaultContextCharWidth = value
            End Set
        End Property

        Public Shared Property DefaultContextAutoWidth As Boolean
            Get
                Return MenuSystemPlugin.DefaultContextAutoWidth
            End Get
            Set(value As Boolean)
                MenuSystemPlugin.DefaultContextAutoWidth = value
            End Set
        End Property

        Public Shared Property DefaultDropdownHostCharWidth As Integer
            Get
                Return MenuSystemPlugin.DefaultDropdownHostCharWidth
            End Get
            Set(value As Integer)
                MenuSystemPlugin.DefaultDropdownHostCharWidth = value
            End Set
        End Property

        Public Shared Property DefaultDropdownItemCharWidth As Integer
            Get
                Return MenuSystemPlugin.DefaultDropdownItemCharWidth
            End Get
            Set(value As Integer)
                MenuSystemPlugin.DefaultDropdownItemCharWidth = value
            End Set
        End Property

        Public Shared Property SubmenuPrefixWidth As Integer
            Get
                Return MenuSystemPlugin.SubmenuPrefixWidth
            End Get
            Set(value As Integer)
                MenuSystemPlugin.SubmenuPrefixWidth = value
            End Set
        End Property

        ' Batch update with internal diagnostic logging
        Public Shared Sub SetContextMenuDefaults(charWidth As Integer, autoWidth As Boolean)
            _plugin?.SetContextMenuDefaults(charWidth, autoWidth)
        End Sub

        Public Shared Sub SetDropdownMenuDefaults(hostCharWidth As Integer, itemCharWidth As Integer)
            _plugin?.SetDropdownMenuDefaults(hostCharWidth, itemCharWidth)
        End Sub

        ' =========================================================================
        ' NAMED DROPDOWN LIFECYCLE (Persistent State)
        ' =========================================================================
        ' CONSTRAINTS:
        '   - menuName unique; duplicate CreateMenu logs warning and returns.
        '   - panel determines spatial quadrant for overlap calculations.
        '   - charWidth < 0 uses static defaults above.
        '   - DeleteMenu triggers UnregisterActiveMenu, restoring collapsed neighbors.

        Public Shared Sub CreateMenu(menuName As String, panel As PanelType,
                                      Optional anchorRow As Integer = 5,
                                      Optional anchorCol As Integer = 10,
                                      Optional hostCharWidth As Integer = -1,
                                      Optional itemCharWidth As Integer = -1)
            _plugin?.CreateMenu(menuName, panel, anchorRow, anchorCol, hostCharWidth, itemCharWidth)
        End Sub

        Public Shared Sub DeleteMenu(menuName As String)
            _plugin?.DeleteMenu(menuName)
        End Sub

        ' =========================================================================
        ' CONTEXT MENU (Singleton-Pooled)
        ' =========================================================================
        ' Only one visible system-wide. New ShowContextMenu destroys previous.
        ' Spatial overlap: Registers for AABB collision; higher ShowOrder dropdowns collapse this.
        ' HideContextMenu is idempotent.

        Public Shared Sub ShowContextMenu(panel As PanelType, clickRow As Integer, clickCol As Integer,
                                           items() As String,
                                           Optional charWidth As Integer = -1,
                                           Optional autoWidth As Boolean? = Nothing)
            _plugin?.ShowContextMenu(panel, clickRow, clickCol, items, charWidth, autoWidth)
        End Sub

        Public Shared Sub HideContextMenu()
            _plugin?.HideContextMenu()
        End Sub

        ' =========================================================================
        ' SUBMENU DEFINITIONS (Declarative Pre-configuration)
        ' =========================================================================
        ' Establish hierarchy before item population. 
        ' DefineContextSubmenu affects global context menu only.
        ' DefineMenuSubmenu affects specific named dropdown only.
        ' ClearContextSubmenus resets global definitions.

        Public Shared Sub DefineContextSubmenu(parentItemName As String, childItems() As String)
            _plugin?.DefineContextSubmenu(parentItemName, childItems)
        End Sub

        Public Shared Sub ClearContextSubmenus()
            _plugin?.ClearContextSubmenus()
        End Sub

        Public Shared Sub DefineMenuSubmenu(menuName As String, parentItemName As String, childItems() As String)
            _plugin?.DefineMenuSubmenu(menuName, parentItemName, childItems)
        End Sub

        ' Identifier check for reserved "__ContextMenu__" constant.
        Public Shared Function IsContextMenu(menuName As String) As Boolean
            Return If(_plugin?.IsContextMenu(menuName), False)
        End Function

        ' =========================================================================
        ' ITEM CRUD (Named Dropdowns Only)
        ' =========================================================================
        ' AddMenuItem: Appends to sequence end.
        ' RemoveMenuItem: O(n) search; logs warning if not found.
        ' ClearMenu: Empties sequence; menu object persists (unlike DeleteMenu).

        Public Shared Sub AddMenuItem(menuName As String, itemName As String)
            _plugin?.AddMenuItem(menuName, itemName)
        End Sub

        Public Shared Sub RemoveMenuItem(menuName As String, itemName As String)
            _plugin?.RemoveMenuItem(menuName, itemName)
        End Sub

        Public Shared Sub ClearMenu(menuName As String)
            _plugin?.ClearMenu(menuName)
        End Sub

        ' Returns snapshot array; modifications do not affect internal state.
        Public Shared Function GetMenuItems(menuName As String) As String()
            Return If(_plugin?.GetMenuItems(menuName), New String() {})
        End Function

        Public Shared Function GetMenuItemCount(menuName As String) As Integer
            Return If(_plugin?.GetMenuItemCount(menuName), 0)
        End Function

        ' =========================================================================
        ' INTRA-MENU REORDER (Atomic Transactions)
        ' =========================================================================
        ' MECHANISM: Retrieve List(Of String), calculate permutation, commit via ReorderItems.
        ' Prevents partial UI states. Publishes MenuItemsReordered on completion.
        ' BEHAVIOR:
        '   - MoveItemUp/Down: No-op if at boundary (logs info).
        '   - MoveItemToIndex: Validates bounds; clamps or aborts if invalid.

        Public Shared Sub MoveItemUp(menuName As String, itemName As String)
            _plugin?.MoveItemUp(menuName, itemName)
        End Sub

        Public Shared Sub MoveItemDown(menuName As String, itemName As String)
            _plugin?.MoveItemDown(menuName, itemName)
        End Sub

        Public Shared Sub MoveItemToIndex(menuName As String, itemName As String, newIndex As Integer)
            _plugin?.MoveItemToIndex(menuName, itemName, newIndex)
        End Sub

        ' NOTE: MenuSortOrder enum defined in MenuSystemPlugin.vb
        Public Shared Sub SortMenuItems(menuName As String, sortOrder As MenuSortOrder)
            _plugin?.SortMenuItems(menuName, sortOrder)
        End Sub

        ' =========================================================================
        ' INTER-MENU TRANSFER
        ' =========================================================================
        ' MOVE (Destructive): Removes from source, inserts to target.
        '   Publishes MenuItemMoved: {SourceMenu, TargetMenu, ItemName, OldIndex, NewIndex}
        ' COPY (Non-destructive): Adds to target while preserving source entry.
        '   No event published. Creates independent value copy.
        ' CONSTRAINT: Both menus must exist; aborts silently if either missing.

        Public Shared Sub MoveItemToMenu(sourceMenu As String, itemName As String, targetMenu As String)
            _plugin?.MoveItemToMenu(sourceMenu, itemName, targetMenu)
        End Sub

        Public Shared Sub MoveItemToMenuAt(sourceMenu As String, itemName As String,
                                            targetMenu As String, targetIndex As Integer)
            _plugin?.MoveItemToMenuAt(sourceMenu, itemName, targetMenu, targetIndex)
        End Sub

        Public Shared Sub CopyItemToMenu(sourceMenu As String, itemName As String, targetMenu As String)
            _plugin?.CopyItemToMenu(sourceMenu, itemName, targetMenu)
        End Sub







        ' =========================================================================
        ' SPATIAL ZONE POOLING (Custom UI & Advanced Layouts)
        ' =========================================================================
        ' EXPOSED FOR CUSTOM UI: The menu system maintains a pool of generic, 
        ' pre-initialized spatial zones. Standard dropdowns and context menus use 
        ' these internally. Advanced consumers (like the CAD Hand HUD) can borrow 
        ' them to build entirely custom UI layouts (grids, panels, radial menus) 
        ' without allocating their own rendering state.
        '
        ' THE BORROWER'S CONTRACT (The "Context Menu Pattern"):
        '   1. Acquire a zone via AcquireZone().
        '   2. Fetch the raw ISpatialZone reference via GetPooledZoneRef().
        '   3. Stretch the zone's "_A_*" margins to your layout using the host API's
        '      MarginJump. Do this ONCE, at build: margin jumping is an expensive
        '      geometry recalculation for the host.
        '      DO NOT TOUCH THE "_H_*" MARGINS. They are the pool's own hidden set,
        '      parked at 0,0 and ALREADY assigned to slot A by CreatePooledZone. You
        '      hide by switching slots (step 5), never by moving margins.
        '   4. Set the zone's .Text property to render content inside your bounds.
        '   5. Use SwitchZoneToMarginSetA (Hide) and B (Show) to toggle visibility.
        '   6. You MUST call ReleaseZone() when done to return it to the pool.
        ' =========================================================================

        Public Shared Function AcquireZone() As String
            Return _plugin?.AcquireZone()
        End Function

        Public Shared Sub ReleaseZone(zoneId As String)
            _plugin?.ReleaseZone(zoneId)
        End Sub

        Public Shared Function GetPooledZoneRef(zoneId As String) As ISpatialZone
            Return _plugin?.GetPooledZoneRef(zoneId)
        End Function

        ' =========================================================================
        ' OVERLAP MANAGEMENT (Passive Furniture & Z-Ordering)
        ' =========================================================================
        ' EXPOSED FOR CUSTOM UI: Allows non-standard UI elements (like a persistent 
        ' HUD) to participate in the menu system's Z-order overlap resolution.
        '
        ' PASSIVE FURNITURE PATTERN:
        '   If you are building a persistent UI that should get out of the way 
        '   when a standard dropdown or context menu opens over it, call 
        '   RegisterActiveMenu with your UI's bounding box and collapse/restore 
        '   callbacks.
        '
        '   CRITICAL: Do NOT call NotifyMenuShown for passive UI. Standard menus 
        '   call NotifyMenuShown to actively collapse intersecting neighbors. By 
        '   omitting this call, your UI becomes "passive furniture": it yields to 
        '   new menus, but never pushes them around.
        ' =========================================================================

        Public Shared Sub RegisterActiveMenu(menuId As String,
                                             boundingBox As ((Integer, Integer, Integer), (Integer, Integer, Integer)),
                                             collapseAction As Action,
                                             restoreAction As Action)
            _plugin?.RegisterActiveMenu(menuId, boundingBox, collapseAction, restoreAction)
        End Sub

        Public Shared Sub UnregisterActiveMenu(menuId As String)
            _plugin?.UnregisterActiveMenu(menuId)
        End Sub

        Public Shared Sub NotifyMenuShown(menuId As String)
            _plugin?.NotifyMenuShown(menuId)
        End Sub

        Public Shared Sub NotifyMenuHidden(menuId As String)
            _plugin?.NotifyMenuHidden(menuId)
        End Sub

        Public Shared Sub UpdateMenuBounds(menuId As String,
                                           newBoundingBox As ((Integer, Integer, Integer), (Integer, Integer, Integer)))
            _plugin?.UpdateMenuBounds(menuId, newBoundingBox)
        End Sub





        ' =========================================================================
        ' CLICK OWNERSHIP (Shared Input Arbitration)
        ' =========================================================================
        ' THE PROBLEM: one input event reaches plugins through TWO geometries - the
        ' host's occlusion-AWARE cell pick (Panel/Row/Col on the payload) and the
        ' aggregator's occlusion-BLIND ray walk (which decides who gets the zone tap).
        ' Anything drawn between eye and wall splits them, and consumers deciding by
        ' different pipelines both claim the same click. This provider gives every
        ' plugin one shared answer, computed in the ray pipeline.
        '
        ' THE CONTRACT:
        '   1. Pass the event payload you were handed. Never synthesise one: the
        '      reference is the cache key.
        '   2. Every caller gets the SAME answer for a given event, whenever asked.
        '   3. The answer is a zone id, or "" for "no furniture owns this click".
        '   4. You need know nothing of dispatch order, of caching, or of other
        '      consumers.
        '   5. Nothing is asked of you regarding zone movement. Park, raise, jump or
        '      release zones from your click handler in any order you please; the
        '      answer was computed from a snapshot taken before your handler ran. This
        '      is settled inside the resolver rather than asked of callers, because
        '      four of the five zone-moving verbs - SwitchZoneToMarginSetA,
        '      SwitchZoneToMarginSetB, SwapZoneMarginSets and MarginJump - are HOST API
        '      on ICurrentApi and never pass through the menu system at all. Only
        '      ReleaseZone is ours. A rule the menu system cannot enforce is a rule it
        '      should not make.
        '
        ' FOREIGN ASSEMBLIES cannot name this class (separate projects; only
        ' Current.PluginApi is shared). They reach these verbs through PluginHub on
        ' the "MenuSystem" handle, using THE SAME NAMES published here:
        '   PluginHub.Exec(menu, "IsClickOwned", evt)
        '   PluginHub.Exec(menu, "ResolveClickOwner", evt)
        ' There is no second handle and no second set of names. The resolver beneath
        ' is Friend, so nothing else is visible to browse or to call by mistake.
        ' =========================================================================

        Public Shared Function ResolveClickOwner(evt As Object) As String
            Return If(_plugin?.ResolveClickOwner(evt), "")
        End Function

        Public Shared Function IsClickOwned(evt As Object) As Boolean
            Return If(_plugin?.IsClickOwned(evt), False)
        End Function

        Public Shared Function IsClickOwnedBy(evt As Object, zoneId As String) As Boolean
            Return If(_plugin?.IsClickOwnedBy(evt, zoneId), False)
        End Function

        Public Shared Function IsClickOwnedByMenu(evt As Object) As Boolean
            Return If(_plugin?.IsClickOwnedByMenu(evt), False)
        End Function

        Public Shared Function ReportClickOwnership(evt As Object) As Dictionary(Of String, Object)
            Return If(_plugin?.ReportClickOwnership(evt), New Dictionary(Of String, Object))
        End Function





    End Class

End Namespace

' =============================================================================
' IMPLEMENTATION NOTES FOR AI/LLM CONSUMPTION
' =============================================================================
'
' THREAD SAFETY CONTRACT
' ----------------------
' - Read operations (GetMenuItems, GetMenuItemCount) thread-safe via ConcurrentDictionary.
' - Write operations internally synchronized but non-atomic from business logic view.
' - EventAggregator publishes synchronously on calling thread; subscribe accordingly.
'
' ERROR HANDLING STRATEGY
' -----------------------
' - Graceful degradation: Methods return empty/zero/false rather than exceptions.
' - Validation failures logged to Console; check Output window for "[MenuSystem]" tags.
' - Duplicate names, missing items, and out-of-bounds indices are warnings, not crashes.
'
' SPATIAL OVERLAP (Implementation Hidden)
' ---------------------------------------
' All menu instances register AABB bounds ((minX,minY,minZ),(maxX,maxY,maxZ)).
' ShowOrderCounter (Long) establishes Z-priority; newest wins collisions.
' Collapsed menus tracked via reference-counted cause set (prevents flicker).
'
' USAGE PATTERNS
' --------------
' Basic:
'   MenuSystemApi.CreateMenu("Tools", PanelType.NorthPanel)
'   MenuSystemApi.AddMenuItem("Tools", "Settings")
'
' Configure then Create:
'   MenuSystemApi.DefaultDropdownHostCharWidth = 12
'   MenuSystemApi.CreateMenu("Wide", PanelType.BottomPanel) ' Inherits 12
'
' Context with Submenu:
'   MenuSystemApi.DefineContextSubmenu("Advanced", New String() {"Debug", "Release"})
'   MenuSystemApi.ShowContextMenu(PanelType.TopPanel, 10, 20, 
'                                 New String() {"Basic", "Advanced"})
'
' Cross-Menu Transfer:
'   MenuSystemApi.MoveItemToMenu("Tools", "Settings", "Favorites")
'   ' Event allows UI animation of item journey
'
' EXTENSION POINTS
' ----------------
' - MenuSystemPlugin can be extended with new public methods; mirror them here as Shared.
' - EventAggregator payloads can be extended with new properties (anonymous object schema).
' =============================================================================