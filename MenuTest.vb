Imports System.Threading
Imports Current.PluginApi

<PluginMetadata("Menu System Consumer", "1.0", "Nasuno", "Demonstrates usage of the Menu System plugin.")>
Public Class MenuSystemConsumerPlugin
    Implements IPlugin

    Public Sub Execute(api As ICurrentApi) Implements IPlugin.Execute

        Dim menu = PluginLocator.Get(Of Object)("MenuSystem")

        If menu Is Nothing Then
            Console.WriteLine("[MenuConsumer] Menu system not found.")
            Return
        End If

        menu.CreateMenu("File", PanelType.NorthPanel, anchorTopRow:=5, anchorLeftCol:=10)
        menu.AddMenuItem("File", "New")
        menu.AddMenuItem("File", "Open...")
        menu.AddMenuItem("File", "Save")
        menu.AddMenuItem("File", "Exit")

        menu.CreateMenu("Edit", PanelType.NorthPanel, anchorTopRow:=5, anchorLeftCol:=70)
        menu.AddMenuItem("Edit", "Undo")
        menu.AddMenuItem("Edit", "Redo")
        menu.AddMenuItem("Edit", "Cut")
        menu.AddMenuItem("Edit", "Copy")
        menu.AddMenuItem("Edit", "Paste")

        menu.CreateMenu("Help", PanelType.NorthPanel, anchorTopRow:=5, anchorLeftCol:=130)
        menu.AddMenuItem("Help", "Documentation")
        menu.AddMenuItem("Help", "About")

        Console.WriteLine("[MenuConsumer] Menus created.")
    End Sub

End Class