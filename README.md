

<br><br>

![](https://s12.gifyu.com/images/bE6hW.png)

<br><br>


> ⚠️ **Note:** The menu system is a WIP and is included for plugin events demonstration. 

A standardized dropdown menu system for creating interactive menus in the spatial environment.<br>
Integrates with the Event Aggregator for hover detection. 

---

&nbsp;&nbsp;Accessing the Plugin<br>
```vb
Dim menu = PluginLocator.Get(Of Object)("MenuSystem")
```

---

API

&nbsp;&nbsp;CreateMenu<br>
```vb
Sub CreateMenu(menuName As String, panel As PanelType,
                    Optional anchorTopRow As Integer = 5, Optional anchorLeftCol As Integer = 10)
```
Create a new dropdown menu host on the specified panel.

&nbsp;&nbsp;AddMenuItem<br>
```vb
Sub AddMenuItem(menuName As String, itemText As String)
```
Add a dropdown item to an existing menu.

&nbsp;&nbsp;RemoveMenuItem<br>
```vb
Sub RemoveMenuItem(menuName As String, itemText As String)
```
Remove a specific item from a menu.

&nbsp;&nbsp;ClearMenu<br>
```vb
Sub ClearMenu(menuName As String)
```
Remove all items from a menu (keeps the host).

&nbsp;&nbsp;DeleteMenu<br>
```vb
Sub DeleteMenu(menuName As String)
```
Completely remove a menu and all its items. 

---

&nbsp;&nbsp;Behavior

Menus auto-expand when observer ray enters the host zone<br>
Menus auto-collapse when observer ray leaves all zones (host + items)<br>
Requires Event Aggregator plugin for hover detection

---

&nbsp;&nbsp;Usage Example

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

&nbsp;&nbsp;Dependencies

 Plugin           | Purpose 
------------------|---------
 Event Aggregator | Provides `SpatialZoneMouseEnter` / `SpatialZoneMouseLeave` events for hover detection 

---

https://github.com/nasuno/Holodeck<br>
https://github.com/nasuno/Holodeck_API<br>
https://github.com/nasuno/Plugin-Satellite-Cubes<br>
https://github.com/nasuno/Plugin-SpatialZone-Demo<br>
https://github.com/nasuno/Plugin-Events
