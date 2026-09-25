using System.Diagnostics;

namespace Noctaxis.Desktop.Services;

public interface IExternalUriLauncher
{
    bool TryOpen(Uri uri, out string? error);
}

public sealed class ShellExternalUriLauncher : IExternalUriLauncher
{
    public bool TryOpen(Uri uri, out string? error)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error = exception.Message;
            return false;
        }
    }
}
