using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Njulf.Core.Camera;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Njulf.Core
{
    public abstract class Game : IDisposable
    {
        private IServiceProvider? _services;
        private IWindow? _window;
        private IInputContext? _inputContext;
        private IRenderer? _renderer;
        private IContentManager? _content;
        private IInputManager? _input;
        private ICamera? _camera;
        private Scene.Scene _scene = null!;
        private bool _isRunning = false;
        private bool _isShuttingDown = false;
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "IDE0052:Remove unread private members", Justification = "Used for initialization tracking")]
        private bool _isInitialized = false;

        public string Name { get; set; } = "Njulf Game";
        public int WindowWidth { get; set; } = 1280;
        public int WindowHeight { get; set; } = 720;
        public string WindowTitle { get; set; } = "Njulf Game";
        public bool VSync { get; set; } = true;
        public bool IsRunning => _isRunning;

        public IServiceProvider? Services => _services;
        public IWindow? Window => _window;
        public IRenderer? Renderer => _renderer;
        public IContentManager? Content => _content;
        public IInputManager? Input => _input;
        public ICamera? Camera => _camera;
        public Scene.Scene Scene => _scene;

        protected Game()
        {
            _scene = new Scene.Scene();
        }

        public void Run()
        {
            if (_isRunning) return;
            _isRunning = true;
            _isShuttingDown = false;

            try
            {
                _window = CreateWindow();
                HookWindowEvents(_window);
                _window.Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.Message);
                throw;
            }
            finally
            {
                Shutdown(disposeWindow: true);
                _isRunning = false;
            }
        }

        protected virtual void Initialize()
        {
            if (_window == null)
                throw new InvalidOperationException("The Silk.NET window must be created before services are initialized.");

            _inputContext = _window.CreateInput();

            var services = new ServiceCollection();
            services.AddSingleton(_window);
            services.AddSingleton(_inputContext);

            ConfigureServices(services);

            services.RemoveAll<IWindow>();
            services.AddSingleton(_window);
            services.RemoveAll<IInputContext>();
            services.AddSingleton(_inputContext);

            _services = services.BuildServiceProvider();

            _renderer = _services.GetService<IRenderer>()!;
            _content = _services.GetService<IContentManager>()!;
            _input = _services.GetService<IInputManager>()!;
            _camera = _services.GetService<ICamera>() ?? CreateDefaultCamera();

            _renderer?.Initialize();
        }

        protected virtual void ConfigureServices(IServiceCollection services)
        {
        }

        protected virtual ICamera CreateDefaultCamera()
        {
            return new FirstPersonCamera(new Vector3(0, 0, 5));
        }

        protected virtual void Load()
        {
        }

        [Obsolete("The game loop is owned by Silk.NET window events. Override Update(float) and Draw() instead.")]
        protected virtual void RunMainLoop()
        {
        }

        [Obsolete("UpdateFrame is called by Silk.NET Update events. Override Update(float) instead.")]
        protected virtual void UpdateFrame()
        {
            _input?.Update();
            Update(1f / 60f);
        }

        [Obsolete("DrawFrame is called by Silk.NET Render events. Override Draw() instead.")]
        protected virtual void DrawFrame()
        {
            if (_renderer?.BeginFrame() != true)
                return;

            Draw();
            _renderer.EndFrame();
        }

        protected virtual void Update(float deltaTime)
        {
            _scene.Update(deltaTime);
        }

        protected virtual void Draw()
        {
        }

        protected virtual void Unload()
        {
            _renderer?.Dispose();
            _scene?.Dispose();
        }

        protected virtual void OnResize(int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;

            WindowWidth = width;
            WindowHeight = height;
            _renderer?.Resize(width, height);
            if (_camera != null)
            {
                _camera.AspectRatio = (float)width / height;
            }
        }

        public void Exit()
        {
            _isRunning = false;
            _window?.Close();
        }

        public void Dispose()
        {
            Exit();
            Shutdown(disposeWindow: true);
            GC.SuppressFinalize(this);
        }

        private IWindow CreateWindow()
        {
            if (WindowWidth <= 0)
                throw new InvalidOperationException("WindowWidth must be greater than zero.");
            if (WindowHeight <= 0)
                throw new InvalidOperationException("WindowHeight must be greater than zero.");

            var options = WindowOptions.DefaultVulkan;
            options.Size = new Vector2D<int>(WindowWidth, WindowHeight);
            options.Title = WindowTitle;
            options.VSync = VSync;

            return Silk.NET.Windowing.Window.Create(options);
        }

        private void HookWindowEvents(IWindow window)
        {
            window.Load += OnWindowLoad;
            window.Update += OnWindowUpdate;
            window.Render += OnWindowRender;
            window.FramebufferResize += OnWindowFramebufferResize;
            window.Closing += OnWindowClosing;
        }

        private void OnWindowLoad()
        {
            Initialize();
            _isInitialized = true;
            Load();
        }

        private void OnWindowUpdate(double deltaSeconds)
        {
            if (!_isRunning)
                return;

            _input?.Update();
            Update((float)deltaSeconds);
        }

        private void OnWindowRender(double deltaSeconds)
        {
            if (!_isRunning || _renderer == null)
                return;

            if (_renderer.BeginFrame() != true)
                return;

            try
            {
                Draw();
            }
            finally
            {
                _renderer.EndFrame();
            }
        }

        private void OnWindowFramebufferResize(Vector2D<int> size)
        {
            OnResize(size.X, size.Y);
        }

        private void OnWindowClosing()
        {
            _isRunning = false;
            Shutdown(disposeWindow: false);
        }

        private void Shutdown(bool disposeWindow)
        {
            if (_isShuttingDown)
                return;

            _isShuttingDown = true;
            try
            {
                if (_isInitialized)
                    Unload();
            }
            finally
            {
                _isInitialized = false;
                if (_services is IDisposable disposableServices)
                    disposableServices.Dispose();
                _services = null;

                _inputContext?.Dispose();
                _inputContext = null;

                _renderer = null;
                _content = null;
                _input = null;
                _camera = null;

                if (disposeWindow)
                {
                    _window?.Dispose();
                    _window = null;
                }

                _isShuttingDown = false;
            }
        }
    }
}
