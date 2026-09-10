using Njulf.Core;
using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Input;

namespace Njulf.ApiExamples;

// Input usage stays entirely on Game.Input; the scene is the existing small model fixture.
internal sealed class InputExample(ExampleOptions options) : ExampleGame(options)
{
    private InputActionContext _gameplay = null!, _menuContext = null!;
    private InputVector2Action _move = null!, _mouseLook = null!, _stickLook = null!, _menuMove = null!;
    private InputFloatAction _throttle = null!, _wheel = null!;
    private InputAction _menuToggle = null!, _jump = null!, _accept = null!, _quit = null!;
    private readonly Dictionary<InputAction, Action> _menuCommands = [];
    private InputRebindSession? _capture;
    private bool _menu;
    private string _text = "";
    private float _speed = 3;
    private Vector2 _lastMenuMove;
    private string BindingPath => Options.BindingsPath ?? Path.Combine(AppContext.BaseDirectory, "input-bindings.json");

    protected override void Load()
    {
        base.Load();
        _gameplay = Input.CreateContext("Gameplay"); _menuContext = Input.CreateContext("Menu");
        _menuToggle = Button("Menu", InputKey.Escape); _menuToggle.AddBinding(new(GamepadButton.Start));
        _quit = Button("Quit", InputKey.F12);
        _jump = Button("Jump", InputKey.Space, _gameplay); _jump.AddBinding(new(GamepadButton.A));
        _move = Input.CreateVector2Action("Move", _gameplay);
        _move.AddBinding(new(new InputBinding(InputKey.A), new InputBinding(InputKey.D), new InputBinding(InputKey.S), new InputBinding(InputKey.W)));
        _move.AddBinding(new(GamepadStick.Left));
        _mouseLook = Input.CreateVector2Action("MouseLook", _gameplay, InputValueMode.Delta);
        _mouseLook.AddBinding(InputVector2Binding.MouseMotion(scale: .002f));
        _stickLook = Input.CreateVector2Action("StickLook", _gameplay); _stickLook.AddBinding(new(GamepadStick.Right));
        _throttle = Input.CreateFloatAction("Throttle", _gameplay); _throttle.AddBinding(new(GamepadAxis.RightTrigger));
        _wheel = Input.CreateFloatAction("SpeedWheel", _gameplay, InputValueMode.Delta); _wheel.AddBinding(new(MouseAxis.WheelY));
        _accept = Button("Accept", InputKey.Enter, _menuContext); _accept.AddBinding(new(GamepadButton.A));
        _menuMove = Input.CreateVector2Action("MenuNavigate", _menuContext);
        _menuMove.AddBinding(new(new InputBinding(InputKey.Left), new InputBinding(InputKey.Right), new InputBinding(InputKey.Down), new InputBinding(InputKey.Up)));
        _menuMove.AddBinding(new(new InputBinding(GamepadButton.DPadLeft), new InputBinding(GamepadButton.DPadRight),
            new InputBinding(GamepadButton.DPadDown), new InputBinding(GamepadButton.DPadUp)));
        Command("RebindJump", InputKey.F2, () => Capture("Jump", () => _jump.BeginRebind(0)));
        Command("RebindForward", InputKey.F3, () => Capture("Move/up", () => _move.BeginRebind(0, InputBindingPart.Up)));
        Command("RebindStick", InputKey.F4, () => Capture("Move/stick", () => _move.BeginRebind(1)));
        Command("SaveBindings", InputKey.F5, () => { Input.SaveBindings(BindingPath); Console.WriteLine($"Saved {BindingPath}"); });
        Command("LoadBindings", InputKey.F6, Reload);
        Command("RebindWheel", InputKey.F7, () => Capture("SpeedWheel", () => _wheel.BeginRebind(0)));
        Command("RebindMouse", InputKey.F8, () => Capture("MouseLook", () => _mouseLook.BeginRebind(0)));
        Command("RebindTrigger", InputKey.F9, () => Capture("Throttle", () => _throttle.BeginRebind(0)));
        Input.TextInput += OnText;
        Reload(); SetMenu(false);
        Console.WriteLine("WASD / left stick: move. Mouse / right stick: look. Right trigger: faster. Wheel: speed. Space / A: jump action.");
        Console.WriteLine("Escape / Start: menu. F12: quit. In menu, type text and press Enter / A; arrows / D-pad navigate.");
        Console.WriteLine("Menu: F2 rebind Jump, F3 forward key, F4 movement stick, F5 save, F6 reload, F7 wheel, F8 mouse look, F9 trigger. Escape cancels capture.");
    }

    protected override async Task LoadAsync(CancellationToken cancellationToken)
    {
        Model model = await Content.LoadAsync<Model>("Assets/tetrahedron.gltf", cancellationToken: cancellationToken);
        Scene.Add(model.CreateInstance());
    }

    private InputAction Button(string name, InputKey key, InputActionContext? context = null)
    {
        var action = Input.CreateAction(name, context); action.AddBinding(new(key)); return action;
    }

    private void Command(string name, InputKey key, Action execute) => _menuCommands.Add(Button(name, key, _menuContext), execute);
    private void Capture(string target, Func<InputRebindSession> begin)
    {
        _capture = begin(); Console.WriteLine($"Binding {target}: press or move a compatible control; Escape cancels.");
    }
    private void Reload()
    {
        try { Console.WriteLine(Input.LoadBindings(BindingPath) ? $"Loaded {BindingPath}" : $"Using defaults; no file at {BindingPath}"); }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or ArgumentException)
        { Console.WriteLine($"Bindings unchanged: {e.Message}"); }
    }
    private void SetMenu(bool menu)
    {
        _menu = menu; Input.ActiveContext = menu ? _menuContext : _gameplay;
        Input.SetCursorMode(menu ? InputCursorMode.Normal : InputCursorMode.Captured);
        Console.WriteLine(menu ? "Menu: gameplay suspended." : "Gameplay active.");
    }
    private void OnText(char character)
    {
        if (!_menu || char.IsControl(character)) return;
        _text += character; Console.WriteLine($"Text: {_text}");
    }

    protected override void Update(GameTime time)
    {
        base.Update(time);
        if (_capture != null)
        {
            if (_capture.Status == InputRebindStatus.Pending) return;
            Console.WriteLine($"Rebinding {_capture.Status}. F5 saves the current bindings.");
            _capture.Dispose(); _capture = null;
        }
        if (_quit.WasPressed) { Exit(); return; }
        if (_menuToggle.WasPressed) { SetMenu(!_menu); return; }
        if (_menu)
        {
            foreach (var (action, execute) in _menuCommands)
                if (action.WasPressed) { execute(); break; }
            if (_accept.WasPressed) { Console.WriteLine($"Accepted: {_text}"); _text = ""; }
            if (!_menuMove.Value.Equals(_lastMenuMove))
            { _lastMenuMove = _menuMove.Value; Console.WriteLine($"Menu navigation: {_lastMenuMove}"); }
            return;
        }
        float dt = (float)time.ElapsedGameTime.TotalSeconds;
        _speed = System.Math.Clamp(_speed + _wheel.Value * .25f, .25f, 20);
        Camera.Position += (Camera.Right * _move.Value.X + Camera.Forward * _move.Value.Y) * (_speed + _throttle.Value * 5) * dt;
        var look = _mouseLook.Value;
        var stick = _stickLook.Value * (2 * dt); // Held stick is a rate; mouse is already a displacement.
        if (Camera is FirstPersonCamera camera) camera.RotateYawPitch(-look.X - stick.X, -look.Y + stick.Y);
        if (_jump.WasPressed) Console.WriteLine("Jump action pressed.");
    }

    protected override void Unload()
    {
        _capture?.Dispose(); Input.TextInput -= OnText;
        Input.SetCursorMode(InputCursorMode.Normal); base.Unload();
    }
}
