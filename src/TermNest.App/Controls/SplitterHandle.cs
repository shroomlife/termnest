using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace TermNest.App.Controls;

/// <summary>
/// Thin draggable surface used as a splitter handle. Inherits from
/// <see cref="Grid"/> (not sealed in WinUI 3) so it can host a templated
/// child element while exposing <see cref="UIElement.ProtectedCursor"/>,
/// which is otherwise inaccessible to consumers. Drag mechanics live on
/// the parent shell — this control's only responsibility is the cursor
/// surface.
/// </summary>
public sealed partial class SplitterHandle : Grid
{
    /// <summary>
    /// The system cursor shown while the pointer is over this handle.
    /// Setting <c>null</c> falls back to the default arrow.
    /// </summary>
    public InputCursor? HandleCursor
    {
        get => ProtectedCursor;
        set => ProtectedCursor = value;
    }
}
