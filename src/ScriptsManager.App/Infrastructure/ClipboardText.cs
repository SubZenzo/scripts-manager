using System.Runtime.InteropServices;
using System.Windows;

namespace ScriptsManager.App.Infrastructure;

public static class ClipboardText
{
    /// <summary>Буфер обмена бывает занят другой программой — тогда несколько попыток, потом сообщение.</summary>
    public static void Set(string text)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return;
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
            catch (COMException ex)
            {
                Dialogs.Info("Не удалось скопировать в буфер обмена: " + ex.Message);
                return;
            }
        }
    }
}
