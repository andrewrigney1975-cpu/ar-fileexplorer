using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace FileExplorer.Helpers;

/// <summary>A draggable divider. Plain <see cref="Microsoft.UI.Xaml.UIElement.ProtectedCursor"/> is
/// only settable from within a UIElement subclass — this exists solely to expose that as a public
/// method so a splitter can show a resize cursor on hover.</summary>
public sealed class SplitterHandle : Grid
{
    public void SetResizeCursor(bool hovering) =>
        ProtectedCursor = hovering
            ? InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast)
            : null;
}
