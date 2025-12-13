# Menu System Plugin Guide

**Plugin:** Menu System v1.0  
**Author:** Nasuno

> ⚠️ **Note:** The menu system is a WIP and is included for plugin events demonstration. 

A standardized dropdown menu system for creating interactive menus in the spatial environment.  Integrates with the Event Aggregator for hover detection. 

---

## Accessing the Plugin

```vb
Dim menu = PluginLocator.Get(Of Object)("MenuSystem")
```

---

## API

### CreateMenu
```vb
Sub CreateMenu(menuName As String, panel As PanelType, Optional anchorTopRow As Integer = 5, Optional anchorLeftCol As Integer = 10)
```
Create a new dropdown menu host on the specified panel.

### AddMenuItem
```vb
Sub AddMenuItem(menuName As String, itemText As String)
```
Add a dropdown item to an existing menu.

### RemoveMenuItem
```vb
Sub RemoveMenuItem(menuName As String, itemText As String)
```
Remove a specific item from a menu.

### ClearMenu
```vb
Sub ClearMenu(menuName As String)
```
Remove all items from a menu (keeps the host).

### DeleteMenu
```vb
Sub DeleteMenu(menuName As String)
```
Completely remove a menu and all its items. 

---

## Behavior

- Menus auto-expand when observer ray enters the host zone
- Menus auto-collapse when observer ray leaves all zones (host + items)
- Requires Event Aggregator plugin for hover detection

---

## Usage Example

```vb
Dim menu = PluginLocator.Get(Of Object)("MenuSystem")

If menu Is Nothing Then
    Console. WriteLine("Menu system not found.")
    Return
End If

' Create a File menu
menu.CreateMenu("File", PanelType.NorthPanel, anchorTopRow:=5, anchorLeftCol:=10)
menu.AddMenuItem("File", "New")
menu.AddMenuItem("File", "Open...")
menu.AddMenuItem("File", "Save")
menu.AddMenuItem("File", "Exit")

' Create an Edit menu
menu.CreateMenu("Edit", PanelType.NorthPanel, anchorTopRow: =5, anchorLeftCol: =70)
menu.AddMenuItem("Edit", "Undo")
menu.AddMenuItem("Edit", "Redo")
menu.AddMenuItem("Edit", "Cut")
menu.AddMenuItem("Edit", "Copy")
menu.AddMenuItem("Edit", "Paste")

' Create a Help menu
menu. CreateMenu("Help", PanelType.NorthPanel, anchorTopRow:=5, anchorLeftCol:=130)
menu.AddMenuItem("Help", "Documentation")
menu.AddMenuItem("Help", "About")
```

---

## Dependencies

 Plugin           | Purpose 
------------------|---------
 Event Aggregator | Provides `SpatialZoneMouseEnter` / `SpatialZoneMouseLeave` events for hover detection 
