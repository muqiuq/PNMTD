using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PNMTD.Sensor.Windows.Ui;

/// <summary>The window's task dialogs and opening files in an editor.</summary>
internal static class Dialogs
{
    public static void Error(IWin32Window owner, ILogger logger, string what, Exception e)
    {
        logger.LogError(e, "{What} failed", what);
        TaskDialog.ShowDialog(owner, new TaskDialogPage { Caption = "PNMT Sensor", Heading = $"{what} failed", Text = e.Message, Icon = TaskDialogIcon.Error });
    }

    public static void Info(IWin32Window owner, string heading, string text) =>
        TaskDialog.ShowDialog(owner, new TaskDialogPage { Caption = "PNMT Sensor", Heading = heading, Text = text, Icon = TaskDialogIcon.Information });

    /// <summary>A question with one action button and Cancel (the default); true when the action was chosen.</summary>
    public static bool Confirm(IWin32Window owner, string heading, string text, string action, bool warning = true)
    {
        var go = new TaskDialogButton(action);
        var cancel = TaskDialogButton.Cancel;
        var page = new TaskDialogPage
        {
            Caption = "PNMT Sensor",
            Heading = heading,
            Text = text,
            Icon = warning ? TaskDialogIcon.Warning : TaskDialogIcon.Information,
            AllowCancel = true,
        };
        page.Buttons.Add(go);
        page.Buttons.Add(cancel);
        page.DefaultButton = cancel;
        return TaskDialog.ShowDialog(owner, page) == go;
    }

    /// <summary>
    /// Opens a script in the default editor: the "edit" verb (PowerShell ISE, or the editor that registered it), else Notepad. Never
    /// the "open" verb, which runs a .ps1 where someone associated it with PowerShell.
    /// </summary>
    public static void Edit(string path)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = true };
        if (info.Verbs.Contains("edit", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                info.Verb = "edit";
                Process.Start(info);
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The verb points to an editor that is gone (ISE removed): Notepad.
            }
        }
        var notepad = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
        notepad.ArgumentList.Add(path);
        Process.Start(notepad);
    }
}
