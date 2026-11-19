using System.Runtime.InteropServices;

namespace WuHealthGuard.Tray;

/// <summary>
/// Hidden window that asks Windows to hold a shutdown/restart while <see cref="BlockReason"/> is set.
/// Best effort: the user (or Windows Update with a forced restart) can still override it.
/// </summary>
/// <remarks>TODO(phase 3 VM test): confirm the reason text shows on the "apps are preventing restart" screen from a tray-only app.</remarks>
internal sealed partial class ShutdownGuardWindow : Form
{
    private const int WmQueryEndSession = 0x0011;
    private string? _reason;

    public ShutdownGuardWindow()
    {
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;
        _ = Handle; // Create the window so it receives WM_QUERYENDSESSION without being shown.
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? BlockReason
    {
        get => _reason;
        set
        {
            _reason = value;
            if (value is null)
            {
                ShutdownBlockReasonDestroy(Handle);
            }
            else
            {
                ShutdownBlockReasonCreate(Handle, value);
            }
        }
    }

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmQueryEndSession && _reason is not null)
        {
            m.Result = 0; // FALSE: please don't end the session.
            return;
        }

        base.WndProc(ref m);
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShutdownBlockReasonCreate(nint hWnd, string pwszReason);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShutdownBlockReasonDestroy(nint hWnd);
}
