using System.Numerics;
using System.Reflection;
using Silk.NET.Input;

namespace MSUIClient;

// The GUI backend polls this mouse before its ordinary NewFrame. Native cursor
// position is neither read nor written, so background protocols never move it.
public class LiveGuiInputProxy : DispatchProxy
{
    private object _source = null!;
    private Func<Vector2> _position = null!;
    private Func<bool> _leftDown = null!;
    private Func<bool> _rightDown = null!;
    private IReadOnlyList<IMouse>? _mice;

    public static IInputContext Wrap(IInputContext source, Func<Vector2> position, Func<bool> down, Func<bool> rightDown)
    {
        var context = Make<IInputContext>(source, position, down, rightDown);
        ((LiveGuiInputProxy)(object)context)._mice = source.Mice
            .Select(mouse => Make<IMouse>(mouse, position, down, rightDown)).ToArray();
        return context;
    }

    private static T Make<T>(T source, Func<Vector2> position, Func<bool> down, Func<bool> rightDown) where T : class
    {
        T proxy = Create<T, LiveGuiInputProxy>();
        var state = (LiveGuiInputProxy)(object)proxy;
        state._source = source;
        state._position = position;
        state._leftDown = down;
        state._rightDown = rightDown;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method is null) throw new InvalidOperationException("Missing input member");
        if (method.Name == "get_Mice") return _mice;
        if (_source is IMouse)
        {
            if (method.Name == "get_Position") return _position();
            if (method.Name == "set_Position") return null;
            if (method.Name == "IsButtonPressed")
                return args?[0] switch
                {
                    MouseButton.Left => _leftDown(),
                    MouseButton.Right => _rightDown(),
                    _ => false,
                };
        }
        return method.Invoke(_source, args);
    }
}
